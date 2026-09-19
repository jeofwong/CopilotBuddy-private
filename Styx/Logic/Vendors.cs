using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Inventory;
using Styx.Logic.Inventory.Frames.Gossip;
using Styx.Logic.Inventory.Frames.MailBox;
using Styx.Logic.Inventory.Frames.Merchant;
using Styx.Logic.Inventory.Frames.Trainer;
using Styx.Logic.POI;
using Styx.Logic.Profiles;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic
{
	/// <summary>
	/// Provides functionality for interacting with vendors, trainers, and mailboxes.
	/// </summary>
	public static class Vendors
	{
		private static readonly TrainerFrame _trainerFrame;
		private static readonly MerchantFrame _merchantFrame;
		private static readonly MailFrame _mailFrame;
		private static readonly GossipFrame _gossipFrame;
		private static bool _sellSessionActive;
		private static object? _sellSessionCandidate;
		private static ItemQuality _sellSessionQualities;
		private static List<string>? _sellSessionProtectedNames;
		private static List<uint>? _sellSessionProtectedIds;
		private static int _sellSessionStackCount;

		public static VendorItemsEventHandler? OnVendorItems;
		public static EventHandler? OnRepairItems;
		public static MailItemsEventHandler? OnMailItems;
		public static BuyItemsEventHandler? OnBuyItems;

		public static bool ForceTrainer { get; set; }
		public static bool ForceSell { get; set; }
		public static bool ForceRepair { get; set; }
		public static bool ForceMail { get; set; }
		public static bool ForceBuy { get; set; }
		public static bool NeedClassTraining { get; set; }

		/// <summary>
		/// Gets or sets whether repair functionality is disabled.
		/// </summary>
		public static bool RepairDisabled { get; set; }

		static Vendors()
		{
			_trainerFrame = new TrainerFrame();
			_merchantFrame = new MerchantFrame();
			_mailFrame = new MailFrame();
			_gossipFrame = new GossipFrame();
			BotEvents.Player.OnLevelUp += OnLevelUp;
			// HB 3.3.5a: NeedClassTraining is ONLY set on level up, not on startup
			// This prevents the bot from going to trainer when all spells are already learned
		}

		/// <summary>
		/// Gets the nearest flight master with taxi available.
		/// </summary>
		public static WoWUnit? NearestFlightMerchant
		{
			get
			{
				// leverage cached units for better performance
				return ObjectManager.CachedUnits
					.Where(u => u.IsFlightMaster && u.InteractType == WoWInteractType.TaxiPathAvailable)
					.OrderBy(u => u.Distance)
					.FirstOrDefault();
			}
		}

		private static void OnLevelUp(BotEvents.Player.LevelUpEventArgs args)
		{
			// Use CharacterSettings because it's bound to the UI checkbox
			if (!CharacterSettings.Instance.TrainNewSkills)
				return;

			// In 3.3.5a, new spells available at even levels
			if (args != null && args.NewLevel % 2 == 0)
			{
				NeedClassTraining = true;
				Logging.Write("New spells available at trainer (level {0})!", args.NewLevel);
			}
		}

		/// <summary>
		/// Trains all available skills at the current trainer.
		/// </summary>
		public static void TrainSkills()
		{
			_trainerFrame.BuyAll();
			NeedClassTraining = false;
			ForceTrainer = false;
			
			// Best-effort refresh. BuyTrainerService(0) is async — the server response
			// (and the WoW client's spellbook update) arrives ~30-50ms later. If this
			// call short-circuits because NumKnownSpells hasn't changed yet, the
			// LEARNED_SPELL_IN_TAB Lua event handler in SpellManager will catch it.
			Styx.Logic.Combat.SpellManager.Refresh();
		}

		/// <summary>
		/// Mails all items that should be mailed.
		/// </summary>
		public static void MailAllItems()
		{
			var items = new List<WoWItem>();
			items.AddRange(InventoryManager.GetItemsToMail());

			if (OnMailItems != null)
			{
				var args = new MailItemsEventArgs { AdditionalItems = new List<WoWItem>() };

				foreach (MailItemsEventHandler handler in OnMailItems.GetInvocationList())
				{
					try
					{
						handler(args);
					}
					catch (Exception ex) when (ex is not OperationCanceledException
						&& ex is not ThreadInterruptedException)
					{
						Logging.WriteException(ex);
						args.AdditionalItems.Clear();
						continue;
					}

					foreach (var item in args.AdditionalItems)
					{
						if (!items.Contains(item))
							items.Add(item);
					}
					args.AdditionalItems.Clear();
				}
			}

			_mailFrame.SendMailWithManyAttachments(LevelbotSettings.Instance.MailRecipient, 0, items.ToArray());
			ForceMail = false;
		}

		/// <summary>
		/// Repairs all items.
		/// </summary>
		public static void RepairAllItems()
		{
			OnRepairItems?.Invoke(null, EventArgs.Empty);
			_merchantFrame.RepairAllItems();
			ForceRepair = false;
		}

		/// <summary>
		/// Sells all items according to profile settings.
		/// </summary>
		public static void SellAllItems()
		{
			if (!StartSellSession())
				return;

			object session = _sellSessionCandidate;
			if (session == null || !_sellSessionActive)
				return;

			_merchantFrame.SellItemQualities(
				_sellSessionQualities,
				_sellSessionProtectedNames ?? Enumerable.Empty<string>(),
				_sellSessionProtectedIds ?? Enumerable.Empty<uint>());
			// The bulk callback may have reset or replaced the admitted session.
			if (!ReferenceEquals(_sellSessionCandidate, session) || !_sellSessionActive)
				return;
			ResetSellSession();
			ForceSell = false;
		}

		internal static bool SellAllItemsStep()
		{
			if (!_sellSessionActive)
			{
				Profile? profile = ProfileManager.CurrentProfile;
				if (!StartSellSession())
					// An aborted candidate cannot finish its caller's service sequence.
					// Preserve only the existing no-profile-at-entry terminal no-op.
					return profile == null && ProfileManager.CurrentProfile == null && !_sellSessionActive;
			}

			return ContinueSellSession();
		}

		private static bool StartSellSession()
		{
			if (_sellSessionActive)
				return true;

			// Each construction attempt owns a distinct identity. Reset or nested
			// admission invalidates older callbacks without touching a replacement.
			object candidate = new object();
			_sellSessionCandidate = candidate;
			ItemQuality qualityMask = ItemQuality.None;
			Profile? currentProfile = ProfileManager.CurrentProfile;

			if (currentProfile == null)
				return false;

			if (currentProfile.SellGrey)
				qualityMask |= ItemQuality.Poor;
			if (currentProfile.SellWhite)
				qualityMask |= ItemQuality.Common;
			if (currentProfile.SellGreen)
				qualityMask |= ItemQuality.Uncommon;
			if (currentProfile.SellBlue)
				qualityMask |= ItemQuality.Rare;
			if (currentProfile.SellPurple)
				qualityMask |= ItemQuality.Epic;

			var protectedNames = new List<string>();
			var protectedIds = new List<uint>();

			protectedNames.AddRange(ProtectedItemsManager.GetAllItemNames());
			protectedIds.AddRange(ProtectedItemsManager.GetAllItemIds());

			// Protect food and drink
			if (uint.TryParse(LevelbotSettings.Instance.FoodName, out uint foodId))
				protectedIds.Add(foodId);
			else
				protectedNames.Add(LevelbotSettings.Instance.FoodName.ToLower());

			if (uint.TryParse(LevelbotSettings.Instance.DrinkName, out uint drinkId))
				protectedIds.Add(drinkId);
			else
				protectedNames.Add(LevelbotSettings.Instance.DrinkName.ToLower());

			// Automatic food/drink mode leaves FoodName and DrinkName empty. Protect
			// every consumable currently in the bags so SellWhite can never dispose
			// of the supplies the rest behavior is actively using.
			foreach (WoWItem consumable in Consumable.GetFood().Concat(Consumable.GetDrinks()))
			{
				if (!protectedIds.Contains(consumable.Entry))
					protectedIds.Add(consumable.Entry);
				if (!protectedNames.Contains(consumable.Name.ToLower()))
					protectedNames.Add(consumable.Name.ToLower());
			}

			// Fire event for plugins to add exclusions
			if (OnVendorItems != null)
			{
				var args = new SellItemsEventArgs
				{
					NameExceptions = new List<string>(),
					IdExceptions = new List<uint>()
				};

				foreach (VendorItemsEventHandler handler in OnVendorItems.GetInvocationList())
				{
					if (!IsSellSessionCandidateCurrent(candidate, currentProfile))
						return false;
					try
					{
						handler(args);
					}
					catch (Exception ex) when (ex is not OperationCanceledException
						&& ex is not ThreadInterruptedException)
					{
						Logging.WriteException(ex);
						args.NameExceptions.Clear();
						args.IdExceptions.Clear();
						if (!IsSellSessionCandidateCurrent(candidate, currentProfile))
							return false;
						continue;
					}

					if (!IsSellSessionCandidateCurrent(candidate, currentProfile))
						return false;

					foreach (string name in args.NameExceptions)
					{
						if (!protectedNames.Contains(name))
							protectedNames.Add(name);
					}

					foreach (uint id in args.IdExceptions)
					{
						if (!protectedIds.Contains(id))
							protectedIds.Add(id);
					}

					args.NameExceptions.Clear();
					args.IdExceptions.Clear();
				}

				protectedNames.AddRange(args.NameExceptions);
				protectedIds.AddRange(args.IdExceptions);
			}

			// Callback work may add protection or change the active profile. Preserve
			// every earlier exclusion, but never publish an old profile's candidate.
			if (!IsSellSessionCandidateCurrent(candidate, currentProfile))
				return false;
			foreach (string name in ProtectedItemsManager.GetAllItemNames())
				if (!protectedNames.Contains(name)) protectedNames.Add(name);
			foreach (uint id in ProtectedItemsManager.GetAllItemIds())
				if (!protectedIds.Contains(id)) protectedIds.Add(id);
			if (!IsSellSessionCandidateCurrent(candidate, currentProfile))
				return false;

			_sellSessionActive = true;
			_sellSessionQualities = qualityMask;
			_sellSessionProtectedNames = protectedNames;
			_sellSessionProtectedIds = protectedIds;
			_sellSessionStackCount = 0;
			return true;
		}

		private static bool IsSellSessionCandidateCurrent(object candidate, Profile profile) =>
			ReferenceEquals(_sellSessionCandidate, candidate) && !_sellSessionActive
			&& ReferenceEquals(ProfileManager.CurrentProfile, profile);

		private static bool ContinueSellSession()
		{
			// A result and its diagnostics belong to the session that requested them.
			// Reentrant callbacks may reset or publish a different session.
			object session = _sellSessionCandidate;
			bool OwnsSession() => session != null && _sellSessionActive
				&& ReferenceEquals(_sellSessionCandidate, session);
			if (!OwnsSession())
				return false;

			int result = _merchantFrame.SellNextItemQualities(
				_sellSessionQualities,
				_sellSessionProtectedNames ?? Enumerable.Empty<string>(),
				_sellSessionProtectedIds ?? Enumerable.Empty<uint>());

			if (!OwnsSession())
				return false;
			if (result < 0)
				return false;

			if (result == 1)
			{
				_sellSessionStackCount++;
				return false;
			}

			if (result == 2)
				return false;

			if (result == 3)
			{
				Logging.WriteDebug("Vendor sale interrupted because the merchant window closed during the scan.");
				if (!OwnsSession())
					return false;
				ResetSellSession();
				return true;
			}

			if (result == 4)
			{
				Logging.Write("Vendor sale pass deferred by the bounded retry guard; no sale acknowledgement is inferred.");
				if (!OwnsSession())
					return false;
				ResetSellSession();
				ForceSell = false;
				return true;
			}
			if (result != 0)
				return false;

			Logging.Write("Vendor sale pass complete: submitted {0} request(s); merchant acceptance is not inferred.", _sellSessionStackCount);
			if (!OwnsSession())
				return false;
			ResetSellSession();
			ForceSell = false;
			return true;
		}

		private static void ResetSellSession()
		{
			_sellSessionCandidate = null;
			_sellSessionActive = false;
			_sellSessionProtectedNames = null;
			_sellSessionProtectedIds = null;
			_sellSessionStackCount = 0;
		}

		/// <summary>
		/// Buys items from vendor based on OnBuyItems event handlers and food/drink settings.
		/// Ported from HB 4.3.4.
		/// </summary>
		public static void BuyItems()
		{
			var itemsToBuy = new Dictionary<uint, int>();

			// Handle OnBuyItems event
			if (OnBuyItems != null)
			{
				var args = new BuyItemsEventArgs();
				foreach (BuyItemsEventHandler handler in OnBuyItems.GetInvocationList())
				{
					try
					{
						handler(args);
						foreach (var kvp in args.BuyItemsIds)
						{
							if (!itemsToBuy.ContainsKey(kvp.Key))
								itemsToBuy.Add(kvp.Key, kvp.Value);
						}
					}
					catch (Exception ex) when (ex is not OperationCanceledException
						&& ex is not ThreadInterruptedException)
					{
						Logging.WriteException(ex);
						args.BuyItemsIds.Clear();
					}
				}
			}

			if (itemsToBuy.Count > 0)
			{
				foreach (var merchantItem in _merchantFrame.GetAllMerchantItems())
				{
					if (itemsToBuy.ContainsKey(merchantItem.ItemId))
						_merchantFrame.BuyItem(merchantItem.Index, itemsToBuy[merchantItem.ItemId]);
				}
			}

			// Handle automatic food/drink buying based on settings (HB 4.3.4 logic)
			Vendor asVendor = BotPoi.Current.AsVendor;
			if (asVendor == null || (asVendor.Type != Vendor.VendorType.Food && asVendor.Type != Vendor.VendorType.Restock))
				return;

			bool usesMana = StyxWoW.Me.PowerType == WoWPowerType.Mana || StyxWoW.Me.Class == WoWClass.Druid;
			bool needsDrink = usesMana && Consumable.GetBestDrink(false) == null &&
			                  CharacterSettings.Instance.DrinkAmount > 0;
			bool needsFood = Consumable.GetBestFood(false) == null &&
			                 CharacterSettings.Instance.FoodAmount > 0;
			ConsumableCandidate bestDrink = needsDrink
				? _merchantFrame.GetBestDrinkCandidateFromVendor()
				: null;
			ConsumableCandidate bestFood = needsFood
				? _merchantFrame.GetBestFoodCandidateFromVendor()
				: null;

			bool missingRequestedConsumable =
				(needsDrink && bestDrink == null) ||
				(needsFood && bestFood == null);
			bool purchased = false;

			if (bestDrink != null)
			{
				Logging.Write("Buying {0}x {1} ({2} mana, max mana {3})",
					CharacterSettings.Instance.DrinkAmount,
					bestDrink.Name,
					bestDrink.ManaRestored,
					StyxWoW.Me.MaxMana);
				purchased |= _merchantFrame.BuyConsumable(
					bestDrink,
					CharacterSettings.Instance.DrinkAmount);
			}

			if (bestFood != null)
			{
				Logging.Write("Buying {0}x {1} ({2} health, max health {3})",
					CharacterSettings.Instance.FoodAmount,
					bestFood.Name,
					bestFood.HealthRestored,
					StyxWoW.Me.MaxHealth);
				purchased |= _merchantFrame.BuyConsumable(
					bestFood,
					CharacterSettings.Instance.FoodAmount);
			}

			if (missingRequestedConsumable)
			{
				Vendor failedVendor = BotPoi.Current.AsVendor;
				Logging.Write("Vendor does not sell the required food or water. Blacklisting {0} for this profile session.",
					failedVendor?.Name ?? "vendor");
				if (failedVendor != null)
					ProfileManager.CurrentProfile?.VendorManager?.Blacklist.Add(failedVendor);
				BotPoi.Clear("Blacklisted Vendor");
			}

			if (purchased)
				StyxWoW.Sleep(2000);
			_merchantFrame.Close();
			ForceBuy = false;
			if (!missingRequestedConsumable)
				BotPoi.Clear("Restocked");
		}
	}
}

