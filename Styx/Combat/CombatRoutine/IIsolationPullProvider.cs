using TreeSharp;

namespace Styx.Combat.CombatRoutine
{
    /// <summary>
    /// Optional routine-owned ranged opener for LevelBot dense-pack isolation.
    /// The bot owns world-risk and retreat movement; the routine owns spell choice.
    /// </summary>
    public interface IIsolationPullProvider
    {
        double IsolationPullDistance { get; }
        Composite CreateIsolationPullBehavior();
    }
}
