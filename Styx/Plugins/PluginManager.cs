#nullable disable
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Styx.Helpers;
using Styx.Plugins.PluginClass;

namespace Styx.Plugins
{
    /// <summary>
    /// Manages plugin loading, compilation, and execution.
    /// </summary>
    public static class PluginManager
    {
        /// <summary>
        /// Gets whether the plugin system is initialized.
        /// </summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// Gets whether plugins are currently being built/compiled.
        /// </summary>
        public static bool IsBuildingPlugins { get; private set; }

        /// <summary>Set during teardown so cleanup-disables don't rewrite the saved EnabledPlugins list.</summary>
        public static bool IsTearingDown { get; set; }

        /// <summary>
        /// Gets all loaded plugins.
        /// </summary>
        public static List<PluginContainer> Plugins { get; private set; }
        private static readonly Dictionary<string, DateTime> SlowPluginLogTimes =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private sealed class PluginSourceCacheEntry
        {
            internal string Fingerprint;
            internal Type[] PluginTypes;
        }

        // Assemblies loaded into the default context cannot be unloaded. Recompiling
        // identical source on each manual Refresh permanently retains another plugin
        // assembly. Cache only a fully-constructed compiled type set and create fresh
        // plugin instances for an unchanged source fingerprint.
        private static readonly object PluginSourceCacheLock = new object();
        private static readonly Dictionary<string, PluginSourceCacheEntry> PluginSourceCache =
            new Dictionary<string, PluginSourceCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> UnavailableEnabledPlugins =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the path to the Plugins directory.
        /// </summary>
        public static string PluginsDirectory => Path.Combine(Logging.ApplicationPath, "Plugins");

        static PluginManager()
        {
            Plugins = new List<PluginContainer>();
        }

        /// <summary>
        /// Pulses all enabled plugins.
        /// </summary>
        internal static void Pulse()
        {
            for (int i = 0; i < Plugins.Count; i++)
            {
                if (Plugins[i].Enabled)
                {
                    PluginContainer container = Plugins[i];
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        container.Plugin.Pulse();
                    }
                    catch (Exception ex)
                    {
                        Logging.WriteException(ex);
                    }
                    finally
                    {
                        timer.Stop();
                        string pluginName = container.Plugin.Name ?? container.Plugin.GetType().Name;
                        SlowPluginLogTimes.TryGetValue(pluginName, out DateTime previousLogUtc);
                        DateTime nowUtc = DateTime.UtcNow;
                        if (ShouldLogSlowPluginPulse(timer.ElapsedMilliseconds, nowUtc, previousLogUtc))
                        {
                            SlowPluginLogTimes[pluginName] = nowUtc;
                            Logging.WriteDiagnostic(
                                "[Pulse] Slow plugin: name={0} elapsed={1}ms",
                                pluginName,
                                timer.ElapsedMilliseconds);
                        }
                    }
                }
            }
        }

        internal static bool ShouldLogSlowPluginPulse(
            long elapsedMilliseconds,
            DateTime nowUtc,
            DateTime previousLogUtc)
        {
            return elapsedMilliseconds >= 20
                && nowUtc - previousLogUtc >= TimeSpan.FromSeconds(10);
        }

        /// <summary>
        /// Initializes the plugin system.
        /// </summary>
        /// <param name="defaultEnabled">Names of plugins to enable by default.</param>
        public static void Initialize(params string[] defaultEnabled)
        {
            if (!IsInitialized)
            {
                RefreshPlugins(defaultEnabled);
                // Note: Plugin.Initialize() is already called by PluginContainer.Enabled setter
                // No need to call it again here
                IsInitialized = true;
            }
        }

        /// <summary>
        /// Updates the EnabledPlugins property in CharacterSettings.
        /// Note: Does NOT save immediately - save is done at app close or bot start/stop (HB 4.3.4 pattern).
        /// </summary>
        public static void UpdateEnabledPlugins()
        {
            // don't rewrite the saved list during build or teardown — both would persist an empty set
            if (IsBuildingPlugins || IsTearingDown)
                return;

            try
            {
                var enabledPluginNames = Plugins
                    .Where(p => p.Enabled)
                    .Select(p => p.Name)
                    .Concat(UnavailableEnabledPlugins)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                
                Helpers.CharacterSettings.Instance.EnabledPlugins = enabledPluginNames;
                // Note: No Save() here - HB 4.3.4 saves at window close and bot start/stop
            }
            catch (Exception ex)
            {
                Helpers.Logging.WriteException(ex);
            }
        }

        /// <summary>
        /// Saves the list of enabled plugins to CharacterSettings (legacy compatibility).
        /// </summary>
        [Obsolete("Use UpdateEnabledPlugins() instead. Saving is handled globally.")]
        public static void SaveEnabledPlugins()
        {
            UpdateEnabledPlugins();
        }

        /// <summary>
        /// Refreshes the plugin list by reloading from the Plugins directory.
        /// </summary>
        /// <param name="defaultEnabled">Names of plugins to enable by default.</param>
        public static void RefreshPlugins(params string[] defaultEnabled)
        {
            if (IsBuildingPlugins)
                return;

            try
            {
                IsBuildingPlugins = true;
                List<PluginContainer> previousPlugins = Plugins.ToList();
                var replacementPlugins = new List<PluginContainer>();
                var requestedEnabled = new HashSet<string>(
                    defaultEnabled ?? Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                bool hadLoadErrors = false;

                // Force garbage collection to release old plugin assemblies
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Scan the main assembly for built-in HBPlugin subclasses (e.g. LeaderPlugin)
                try
                {
                    foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
                    {
                        if (!type.IsAbstract && typeof(HBPlugin).IsAssignableFrom(type))
                        {
                            HBPlugin plugin = (HBPlugin)Activator.CreateInstance(type);
                            replacementPlugins.Add(new PluginContainer(plugin, false));
                        }
                    }
                }
                    catch (Exception ex)
                    {
                        hadLoadErrors = true;
                        Logging.WriteException(ex);
                }

                string pluginsPath = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "Plugins");

                if (!Directory.Exists(pluginsPath))
                {
                    Directory.CreateDirectory(pluginsPath);
                    Logging.Write("No plugins found. Place plugins in the Plugins directory.");
                    return;
                }

                var files = new List<string>();
                files.AddRange(Directory.GetFiles(pluginsPath, "*.cs", SearchOption.TopDirectoryOnly));
                files.AddRange(Directory.GetDirectories(pluginsPath, "*", SearchOption.TopDirectoryOnly));

                if (files.Count == 0)
                {
                    Logging.Write("No plugins found. Place plugins in the Plugins directory.");
                    return;
                }

                for (int i = 0; i < files.Count; i++)
                {
                    try
                    {
                        List<HBPlugin> loadedPlugins = LoadPluginPathWithCache(
                            files[i], CompileAndLoadFrom);
                        foreach (HBPlugin plugin in loadedPlugins)
                        {
                            replacementPlugins.Add(new PluginContainer(plugin, false));
                        }
                    }
                    catch (CompilerErrorsException ex)
                    {
                        hadLoadErrors = true;
                        Logging.Write("Plugin from {0} could not be compiled. Compiler errors:", files[i]);
                        Logging.Write(ex.ToString());
                    }
                    catch (Exception ex)
                    {
                        hadLoadErrors = true;
                        Logging.Write("Error loading plugin: {0}", files[i]);
                        Logging.WriteException(ex);
                    }
                }

                if (hadLoadErrors && previousPlugins.Count > 0)
                {
                    foreach (PluginContainer replacement in replacementPlugins)
                    {
                        try { replacement.Plugin.Dispose(); } catch { }
                    }
                    Logging.Write("Plugin refresh failed; keeping the previous {0} loaded plugins.", previousPlugins.Count);
                    throw new InvalidOperationException("One or more plugins failed to compile or load; the previous plugin set was preserved.");
                }

                foreach (PluginContainer previous in previousPlugins)
                {
                    if (previous.Enabled)
                        previous.Enabled = false;
                }

                Plugins = replacementPlugins;
                foreach (PluginContainer container in Plugins)
                {
                    if (requestedEnabled.Contains(container.Name))
                        container.Enabled = true;
                }

                UnavailableEnabledPlugins.Clear();
                if (hadLoadErrors)
                {
                    foreach (string requestedName in requestedEnabled)
                    {
                        if (!Plugins.Any(p => string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
                            UnavailableEnabledPlugins.Add(requestedName);
                    }
                }

                Logging.Write("Plugin loading complete. {0} plugins loaded.", Plugins.Count);
                
                if (Plugins.Count == 0)
                {
                    Logging.Write("No plugins found. Place plugins in the Plugins directory.");
                }
                else
                {
                    Logging.Write(hadLoadErrors
                        ? "Plugins loaded with errors; unavailable enabled plugins were preserved in settings."
                        : "Plugins refreshed successfully.");
                }
            }
            finally
            {
                IsBuildingPlugins = false;
            }
        }

        internal static string ComputePluginSourceFingerprint(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Plugin source path is required.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            string root;
            string[] inputs;
            if (File.Exists(fullPath))
            {
                if (!string.Equals(Path.GetExtension(fullPath), ".cs", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Plugin source file must be C#.");
                root = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
                inputs = new[] { fullPath };
            }
            else if (Directory.Exists(fullPath))
            {
                root = fullPath;
                inputs = Directory.GetFiles(fullPath, "*.cs", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(fullPath, "*.resx", SearchOption.AllDirectories))
                    .OrderBy(file => Path.GetRelativePath(root, file), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(file => Path.GetRelativePath(root, file), StringComparer.Ordinal)
                    .ToArray();
            }
            else
            {
                throw new FileNotFoundException("Plugin source path was not found.", fullPath);
            }

            using (var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                foreach (string input in inputs)
                {
                    string relative = Path.GetRelativePath(root, input)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    byte[] name = Encoding.UTF8.GetBytes(relative);
                    manifest.AppendData(BitConverter.GetBytes(name.Length));
                    manifest.AppendData(name);

                    byte[] digest;
                    using (var stream = new FileStream(
                        input, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var sha = SHA256.Create())
                        digest = sha.ComputeHash(stream);

                    manifest.AppendData(BitConverter.GetBytes(digest.Length));
                    manifest.AppendData(digest);
                }

                return BitConverter.ToString(manifest.GetHashAndReset())
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }

        internal static List<HBPlugin> LoadPluginPathWithCache(
            string path,
            Func<string, List<HBPlugin>> compiler)
        {
            if (compiler == null)
                throw new ArgumentNullException(nameof(compiler));

            string key = Path.GetFullPath(path);
            string before = ComputePluginSourceFingerprint(key);
            lock (PluginSourceCacheLock)
            {
                PluginSourceCacheEntry cached;
                if (PluginSourceCache.TryGetValue(key, out cached)
                    && cached != null
                    && string.Equals(cached.Fingerprint, before, StringComparison.Ordinal))
                {
                    return InstantiatePluginTypes(cached.PluginTypes);
                }
            }

            // Compiler exceptions deliberately escape. The last valid cache entry
            // remains untouched, so restoring those bytes can reuse it immediately.
            List<HBPlugin> loaded = compiler(path) ?? new List<HBPlugin>();
            string after = ComputePluginSourceFingerprint(key);

            Type[] completeTypes;
            if (string.Equals(before, after, StringComparison.Ordinal)
                && TryGetCompletePluginTypes(loaded, out completeTypes))
            {
                lock (PluginSourceCacheLock)
                {
                    PluginSourceCache[key] = new PluginSourceCacheEntry
                    {
                        Fingerprint = after,
                        PluginTypes = completeTypes
                    };
                }
            }

            // If input bytes changed during compilation, this result may be used for
            // the current refresh but is never reusable for either observed revision.
            return loaded;
        }

        private static bool TryGetCompletePluginTypes(
            IList<HBPlugin> loaded,
            out Type[] pluginTypes)
        {
            pluginTypes = Type.EmptyTypes;
            if (loaded == null || loaded.Count == 0 || loaded.Any(plugin => plugin == null))
                return false;

            Type[] loadedTypes = loaded.Select(plugin => plugin.GetType()).Distinct().ToArray();
            try
            {
                Type[] declaredTypes = loadedTypes
                    .Select(type => type.Assembly)
                    .Distinct()
                    .SelectMany(assembly => assembly.GetTypes())
                    .Where(type => type != null && type.IsClass && !type.IsAbstract
                        && typeof(HBPlugin).IsAssignableFrom(type))
                    .Distinct()
                    .ToArray();

                // DllLoader logs constructor failures and omits those instances. Do not
                // cache a partial type set or an unchanged refresh would stop retrying
                // the previously failing constructor.
                if (declaredTypes.Length == 0
                    || declaredTypes.Length != loadedTypes.Length
                    || declaredTypes.Except(loadedTypes).Any())
                    return false;

                pluginTypes = declaredTypes;
                return true;
            }
            catch (ReflectionTypeLoadException)
            {
                return false;
            }
        }

        private static List<HBPlugin> InstantiatePluginTypes(IEnumerable<Type> types)
        {
            var result = new List<HBPlugin>();
            if (types == null)
                return result;

            foreach (Type type in types)
            {
                if (type == null || type.IsAbstract || !typeof(HBPlugin).IsAssignableFrom(type))
                    continue;
                try
                {
                    result.Add((HBPlugin)Activator.CreateInstance(type));
                }
                catch (TargetInvocationException ex)
                {
                    Logging.Write("Could not construct instance of {0}. Exception was thrown: Exception:", type.Name);
                    Logging.Write(ex.InnerException == null ? "Unknown" : ex.InnerException.Message);
                }
                catch (Exception ex)
                {
                    Logging.WriteException(ex);
                }
            }
            return result;
        }

        /// <summary>
        /// Compiles and loads plugins from a path.
        /// </summary>
        /// <param name="path">The path to compile from (file or directory).</param>
        /// <returns>List of loaded plugins.</returns>
        public static List<HBPlugin> CompileAndLoadFrom(string path)
        {
            var classCollection = new ClassCollection<HBPlugin>();
            CompilerResults compilerResults;
            classCollection.CompileAndLoadFrom(path, out compilerResults);

            if (compilerResults != null && compilerResults.Errors.HasErrors)
            {
                throw new CompilerErrorsException(Utilities.FormatCompilerErrors(compilerResults));
            }

            return classCollection;
        }
    }
}
