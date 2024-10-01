using Content.Server.Administration.Logs;
using Content.Server.Ensnaring;
using Content.Shared.Cuffs;
using Content.Shared.Cuffs.Components;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Ensnaring.Components;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Popups;
using Content.Shared.Strip;
using Content.Shared.Strip.Components;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using System.Linq;
using System.Diagnostics;

namespace Content.Server.Strip
{
    public sealed class StrippableSystem : SharedStrippableSystem
    {
        [Dependency] private readonly InventorySystem _inventorySystem = default!;
        [Dependency] private readonly EnsnareableSystem _ensnaringSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;

        [Dependency] private readonly SharedCuffableSystem _cuffableSystem = default!;
        [Dependency] private readonly SharedDoAfterSystem _doAfterSystem = default!;
        [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
        [Dependency] private readonly SharedPopupSystem _popupSystem = default!;

        [Dependency] private readonly IAdminLogManager _adminLogger = default!;

        // TODO: ECS popups. Not all of these have ECS equivalents yet.

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<StrippableComponent, GetVerbsEvent<Verb>>(AddStripVerb);
            SubscribeLocalEvent<StrippableComponent, GetVerbsEvent<ExamineVerb>>(AddStripExamineVerb);

            // BUI
            SubscribeLocalEvent<StrippableComponent, StrippingSlotButtonPressed>(OnStripButtonPressed);
            SubscribeLocalEvent<EnsnareableComponent, StrippingEnsnareButtonPressed>(OnStripEnsnareMessage);

            // DoAfters
            SubscribeLocalEvent<HandsComponent, DoAfterAttemptEvent<StrippableDoAfterEvent>>(OnStrippableDoAfterRunning);
            SubscribeLocalEvent<HandsComponent, StrippableDoAfterEvent>(OnStrippableDoAfterFinished);
        }

        private void AddStripVerb(EntityUid uid, StrippableComponent component, GetVerbsEvent<Verb> args)
        {
            if (args.Hands == null || !args.CanAccess || !args.CanInteract || args.Target == args.User)
                return;

            if (!HasComp<ActorComponent>(args.User))
                return;

            Verb verb = new()
            {
                Text = Loc.GetString("strip-verb-get-data-text"),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/outfit.svg.192dpi.png")),
                Act = () => StartOpeningStripper(args.User, (uid, component), true),
            };

            args.Verbs.Add(verb);
        }

        private void AddStripExamineVerb(EntityUid uid, StrippableComponent component, GetVerbsEvent<ExamineVerb> args)
        {
            if (args.Hands == null || !args.CanAccess || !args.CanInteract || args.Target == args.User)
                return;

            if (!HasComp<ActorComponent>(args.User))
                return;

            ExamineVerb verb = new()
            {
                Text = Loc.GetString("strip-verb-get-data-text"),
                Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/outfit.svg.192dpi.png")),
                Act = () => StartOpeningStripper(args.User, (uid, component), true),
                Category = VerbCategory.Examine,
            };

            args.Verbs.Add(verb);
        }

        private void OnActivateInWorld(EntityUid uid, StrippableComponent component, ActivateInWorldEvent args)
        {
            if (args.Target == args.User)
                return;

            if (!HasComp<ActorComponent>(args.User))
                return;

            StartOpeningStripper(args.User, (uid, component));
        }

        private void OnStripEnsnareMessage(EntityUid uid, EnsnareableComponent component, StrippingEnsnareButtonPressed args)
        {
            if (args.Actor is not { Valid: true } user)
                return;

            foreach (var entity in component.Container.ContainedEntities)
            {
                if (!TryComp<EnsnaringComponent>(entity, out var ensnaring))
                    continue;

                _ensnaringSystem.TryFree(uid, user, entity, ensnaring);
                return;
            }
        }

        private void OnStripButtonPressed(Entity<StrippableComponent> strippable, ref StrippingSlotButtonPressed args)
        {
            if (args.Session.AttachedEntity is not { Valid: true } user ||
                !TryComp<HandsComponent>(user, out var userHands) ||
                !TryComp<HandsComponent>(strippable.Owner, out var targetHands))
                return;

            if (args.IsHand)
            {
                StripHand(user, strippable, args.Slot, userHands, targetHands);
                return;
            }

            if (!TryComp<InventoryComponent>(strippable, out var inventory))
                return;

            var hasEnt = _inventorySystem.TryGetSlotEntity(strippable, args.Slot, out var held, inventory);

            if (userHands.ActiveHandEntity != null && !hasEnt)
                StartStripInsertInventory(user, strippable.Owner, userHands.ActiveHandEntity.Value, args.Slot, userHands);
            else if (userHands.ActiveHandEntity == null && hasEnt)
                StartStripRemoveInventory(user, strippable.Owner, held!.Value, args.Slot);
        }

        private void StripHand(
            EntityUid user,
            Entity<StrippableComponent> target,
            string handId,
            HandsComponent? userHands = null,
            HandsComponent? targetHands = null)
        {
            if (!Resolve(user, ref userHands) ||
                !Resolve(target, ref targetHands))
                return;

            if (!_handsSystem.TryGetHand(target.Owner, handId, out var handSlot))
                return;

            // Is the target a handcuff?
            if (TryComp<VirtualItemComponent>(handSlot.HeldEntity, out var virtualItem) &&
                TryComp<CuffableComponent>(target.Owner, out var cuffable) &&
                _cuffableSystem.GetAllCuffs(cuffable).Contains(virtualItem.BlockingEntity))
            {
                _cuffableSystem.TryUncuff(target.Owner, user, virtualItem.BlockingEntity, cuffable);
                return;
            }

            if (userHands.ActiveHandEntity != null && handSlot.HeldEntity == null)
                StartStripInsertHand(user, target.Owner, userHands.ActiveHandEntity.Value, handId, userHands, targetHands, target.Comp);
            else if (userHands.ActiveHandEntity == null && handSlot.HeldEntity != null)
                StartStripRemoveHand(user, target.Owner, handSlot.HeldEntity.Value, handId, userHands, targetHands, target.Comp);
        }

        private void OnStripEnsnareMessage(EntityUid uid, EnsnareableComponent component, StrippingEnsnareButtonPressed args)
        {
            if (args.Session.AttachedEntity is not { Valid: true } user)
                return;

            foreach (var entity in component.Container.ContainedEntities)
            {
                if (!TryComp<EnsnaringComponent>(entity, out var ensnaring))
                    continue;

                _ensnaringSystem.TryFree(uid, user, entity, ensnaring);
                return;
            }
        }

        /// <summary>
        ///     Checks whether the item is in a user's active hand and whether it can be inserted into the inventory slot.
        /// </summary>
        private bool CanStripInsertInventory(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string slot,
            HandsComponent userHands)
        {
            if (userHands.ActiveHand == null)
                return false;

            if (userHands.ActiveHandEntity == null)
                return false;

            if (userHands.ActiveHandEntity != held)
                return false;

            if (!_handsSystem.CanDropHeld(user, userHands.ActiveHand))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-cannot-drop"), user);
                return false;
            }

            if (_inventorySystem.TryGetSlotEntity(target, slot, out _))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-item-slot-occupied", ("owner", target)), user);
                return false;
            }

            if (!_inventorySystem.CanEquip(user, target, held, slot, out _))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-cannot-equip-message", ("owner", target)), user);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Begins a DoAfter to insert the item in the user's active hand into the inventory slot.
        /// </summary>
        private void StartStripInsertInventory(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string slot,
            HandsComponent? userHands = null)
        {
            if (!Resolve(user, ref userHands))
                return;

            if (!CanStripInsertInventory(user, target, held, slot, userHands))
                return;

            if (!_inventorySystem.TryGetSlot(target, slot, out var slotDef))
            {
                Log.Error($"{ToPrettyString(user)} attempted to take an item from a non-existent inventory slot ({slot}) on {ToPrettyString(target)}");
                return;
            }

            var (time, stealth) = GetStripTimeModifiers(user, target, slotDef.StripTime);

            if (!stealth)
                _popupSystem.PopupEntity(Loc.GetString("strippable-component-alert-owner-insert", ("user", Identity.Entity(user, EntityManager)), ("item", userHands.ActiveHandEntity!.Value)), target, target, PopupType.Large);

            var prefix = stealth ? "stealthily " : "";
            _adminLogger.Add(LogType.Stripping, LogImpact.Low, $"{ToPrettyString(user):actor} is trying to {prefix}place the item {ToPrettyString(held):item} in {ToPrettyString(target):target}'s {slot} slot");

            var doAfterArgs = new DoAfterArgs(EntityManager, user, time, new StrippableDoAfterEvent(true, true, slot), user, target, held)
            {
                Hidden = stealth,
                AttemptFrequency = AttemptFrequency.EveryTick,
                BreakOnDamage = true,
                BreakOnMove = true,
                NeedHand = true,
                DuplicateCondition = DuplicateConditions.SameTool
            };

            _doAfterSystem.TryStartDoAfter(doAfterArgs);
        }

        /// <summary>
        ///     Inserts the item in the user's active hand into the inventory slot.
        /// </summary>
        private void StripInsertInventory(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string slot,
            HandsComponent? userHands = null)
        {
            if (!Resolve(user, ref userHands))
                return;

            if (!CanStripInsertInventory(user, target, held, slot, userHands))
                return;

            if (!_handsSystem.TryDrop(user, handsComp: userHands))
                return;

            _inventorySystem.TryEquip(user, target, held, slot);
            _adminLogger.Add(LogType.Stripping, LogImpact.Medium, $"{ToPrettyString(user):actor} has placed the item {ToPrettyString(held):item} in {ToPrettyString(target):target}'s {slot} slot");
        }

        /// <summary>
        ///     Checks whether the item can be removed from the target's inventory.
        /// </summary>
        private bool CanStripRemoveInventory(
            EntityUid user,
            EntityUid target,
            EntityUid item,
            string slot)
        {
            if (!_inventorySystem.TryGetSlotEntity(target, slot, out var slotItem))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-item-slot-free-message", ("owner", target)), user);
                return false;
            }

            if (slotItem != item)
                return false;

            if (!_inventorySystem.CanUnequip(user, target, slot, out var reason))
            {
                _popupSystem.PopupCursor(Loc.GetString(reason), user);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Begins a DoAfter to remove the item from the target's inventory and insert it in the user's active hand.
        /// </summary>
        private void StartStripRemoveInventory(
            EntityUid user,
            EntityUid target,
            EntityUid item,
            string slot)
        {
            if (!CanStripRemoveInventory(user, target, item, slot))
                return;

            if (!_inventorySystem.TryGetSlot(target, slot, out var slotDef))
            {
                Log.Error($"{ToPrettyString(user)} attempted to take an item from a non-existent inventory slot ({slot}) on {ToPrettyString(target)}");
                return;
            }

            var (time, stealth) = GetStripTimeModifiers(user, target, slotDef.StripTime);

            if (!stealth)
            {
                if (slotDef.StripHidden)
                    _popupSystem.PopupEntity(Loc.GetString("strippable-component-alert-owner-hidden", ("slot", slot)), target, target, PopupType.Large);
                else
                    _popupSystem.PopupEntity(Loc.GetString("strippable-component-alert-owner", ("user", Identity.Entity(user, EntityManager)), ("item", item)), target, target, PopupType.Large);
            }

            var prefix = stealth ? "stealthily " : "";
            _adminLogger.Add(LogType.Stripping, LogImpact.Low, $"{ToPrettyString(user):actor} is trying to {prefix}strip the item {ToPrettyString(item):item} from {ToPrettyString(target):target}'s {slot} slot");

            var doAfterArgs = new DoAfterArgs(EntityManager, user, time, new StrippableDoAfterEvent(false, true, slot), user, target, item)
            {
                Hidden = stealth,
                AttemptFrequency = AttemptFrequency.EveryTick,
                BreakOnDamage = true,
                BreakOnMove = true,
                NeedHand = true,
                BreakOnHandChange = false, // Allow simultaneously removing multiple items.
                DuplicateCondition = DuplicateConditions.SameTool
            };

            _doAfterSystem.TryStartDoAfter(doAfterArgs);
        }

        /// <summary>
        ///     Removes the item from the target's inventory and inserts it in the user's active hand.
        /// </summary>
        private void StripRemoveInventory(
            EntityUid user,
            Entity<HandsComponent?> target,
            EntityUid item,
            string slot,
            bool stealth)
        {
            if (!CanStripRemoveInventory(user, target, item, slot))
                return;

            if (!_inventorySystem.TryUnequip(user, target, slot))
                return;

            RaiseLocalEvent(item, new DroppedEvent(user), true); // Gas tank internals etc.

            _handsSystem.PickupOrDrop(user, item, animateUser: stealth, animate: stealth);
            _adminLogger.Add(LogType.Stripping, LogImpact.Medium, $"{ToPrettyString(user):actor} has stripped the item {ToPrettyString(item):item} from {ToPrettyString(target):target}'s {slot} slot");
        }

        /// <summary>
        ///     Checks whether the item in the user's active hand can be inserted into one of the target's hands.
        /// </summary>
        private bool CanStripInsertHand(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string handName,
            HandsComponent userHands,
            HandsComponent? targetHands = null)
        {
            if (!Resolve(target, ref targetHands))
                return false;

            if (userHands.ActiveHand == null)
                return false;

            if (userHands.ActiveHandEntity == null)
                return false;

            if (userHands.ActiveHandEntity != held)
                return false;

            if (!_handsSystem.CanDropHeld(user, userHands.ActiveHand))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-cannot-drop"), user);
                return false;
            }

            if (!_handsSystem.TryGetHand(target, handName, out var handSlot, targetHands) ||
                !_handsSystem.CanPickupToHand(target, userHands.ActiveHandEntity.Value, handSlot, checkActionBlocker: false, targetHands))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-cannot-put-message", ("owner", target)), user);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Begins a DoAfter to insert the item in the user's active hand into one of the target's hands.
        /// </summary>
        private void StartStripInsertHand(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string handName,
            HandsComponent? userHands = null,
            HandsComponent? targetHands = null,
            StrippableComponent? strippable = null)
        {
            if (!Resolve(user, ref userHands) ||
                !Resolve(target, ref targetHands) ||
                !Resolve(target, ref strippable))
                return;

            if (!CanStripInsertHand(user, target, held, handName, userHands, targetHands))
                return;

            var (time, stealth) = GetStripTimeModifiers(user, target, strippable.HandStripDelay);

            var prefix = stealth ? "stealthily " : "";
            _adminLogger.Add(LogType.Stripping, LogImpact.Low, $"{ToPrettyString(user):actor} is trying to {prefix}place the item {ToPrettyString(held):item} in {ToPrettyString(target):target}'s hands");

            var doAfterArgs = new DoAfterArgs(EntityManager, user, time, new StrippableDoAfterEvent(true, false, handName), user, target, held)
            {
                Hidden = stealth,
                AttemptFrequency = AttemptFrequency.EveryTick,
                BreakOnDamage = true,
                BreakOnMove = true,
                NeedHand = true,
                DuplicateCondition = DuplicateConditions.SameTool
            };

            _doAfterSystem.TryStartDoAfter(doAfterArgs);
        }

        /// <summary>
        ///     Places the item in the user's active hand into one of the target's hands.
        /// </summary>
        private void StripInsertHand(
            EntityUid user,
            EntityUid target,
            EntityUid held,
            string handName,
            bool stealth,
            HandsComponent userHands,
            HandsComponent? targetHands = null)
        {
            if (!Resolve(target, ref targetHands))
                return;

            _handsSystem.TryDrop(user, checkActionBlocker: false, handsComp: userHands);
            _handsSystem.TryPickup(target, held, handName, checkActionBlocker: false, animateUser: stealth, animate: stealth, handsComp: targetHands);
            _adminLogger.Add(LogType.Stripping, LogImpact.Medium, $"{ToPrettyString(user):actor} has placed the item {ToPrettyString(held):item} in {ToPrettyString(target):target}'s hands");

            // Hand update will trigger strippable update.
        }

        /// <summary>
        ///     Checks whether the item is in the target's hand and whether it can be dropped.
        /// </summary>
        private bool CanStripRemoveHand(
            EntityUid user,
            EntityUid target,
            EntityUid item,
            string handName,
            HandsComponent? targetHands = null)
        {
            if (!Resolve(target, ref targetHands))
                return false;

            if (!_handsSystem.TryGetHand(target, handName, out var handSlot, targetHands))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-item-slot-free-message", ("owner", target)), user);
                return false;
            }

            if (HasComp<VirtualItemComponent>(handSlot.HeldEntity))
                return false;

            if (handSlot.HeldEntity == null)
                return false;

            if (handSlot.HeldEntity != item)
                return false;

            if (!_handsSystem.CanDropHeld(target, handSlot, false))
            {
                _popupSystem.PopupCursor(Loc.GetString("strippable-component-cannot-drop-message", ("owner", target)), user);
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Begins a DoAfter to remove the item from the target's hand and insert it in the user's active hand.
        /// </summary>
        private void StartStripRemoveHand(
            EntityUid user,
            EntityUid target,
            EntityUid item,
            string handName,
            HandsComponent? userHands = null,
            HandsComponent? targetHands = null,
            StrippableComponent? strippable = null)
        {
            if (!Resolve(user, ref userHands) ||
                !Resolve(target, ref targetHands) ||
                !Resolve(target, ref strippable))
                return;

            if (!CanStripRemoveHand(user, target, item, handName, targetHands))
                return;

            var (time, stealth) = GetStripTimeModifiers(user, target, strippable.HandStripDelay);

            if (!stealth)
                _popupSystem.PopupEntity( Loc.GetString("strippable-component-alert-owner", ("user", Identity.Entity(user, EntityManager)), ("item", item)), target, target);

            var prefix = stealth ? "stealthily " : "";
            _adminLogger.Add(LogType.Stripping, LogImpact.Low, $"{ToPrettyString(user):actor} is trying to {prefix}strip the item {ToPrettyString(item):item} from {ToPrettyString(target):target}'s hands");

            var doAfterArgs = new DoAfterArgs(EntityManager, user, time, new StrippableDoAfterEvent(false, false, handName), user, target, item)
            {
                Hidden = stealth,
                AttemptFrequency = AttemptFrequency.EveryTick,
                BreakOnDamage = true,
                BreakOnTargetMove = true,
                BreakOnUserMove = true,
                NeedHand = true,
                BreakOnHandChange = false, // Allow simultaneously removing multiple items.
                DuplicateCondition = DuplicateConditions.SameTool
            };

            _doAfterSystem.TryStartDoAfter(doAfterArgs);
        }

        /// <summary>
        ///     Takes the item from the target's hand and inserts it in the user's active hand.
        /// </summary>
        private void StripRemoveHand(
            EntityUid user,
            EntityUid target,
            EntityUid item,
            bool stealth,
            HandsComponent userHands,
            HandsComponent? targetHands = null)
        {
            if (!Resolve(target, ref targetHands))
                return;

            _handsSystem.TryDrop(target, item, checkActionBlocker: false, handsComp: targetHands);
            _handsSystem.PickupOrDrop(user, item, animateUser: stealth, animate: stealth, handsComp: userHands);
            _adminLogger.Add(LogType.Stripping, LogImpact.Medium, $"{ToPrettyString(user):actor} has stripped the item {ToPrettyString(item):item} from {ToPrettyString(target):target}'s hands");

            // Hand update will trigger strippable update.
        }

        private void OnStrippableDoAfterRunning(Entity<HandsComponent> entity, ref DoAfterAttemptEvent<StrippableDoAfterEvent> ev)
        {
            var args = ev.DoAfter.Args;

            Debug.Assert(entity.Owner == args.User);
            Debug.Assert(args.Target != null);
            Debug.Assert(args.Used != null);
            Debug.Assert(ev.Event.SlotOrHandName != null);

            if (ev.Event.InventoryOrHand)
            {
                if (ev.Event.InsertOrRemove &&
                    !CanStripInsertInventory(args.User, args.Target.Value, args.Used.Value, ev.Event.SlotOrHandName, entity.Comp) ||
                    !CanStripRemoveInventory(args.User, args.Target.Value, args.Used.Value, ev.Event.SlotOrHandName))
                        ev.Cancel();
            }
            else
            {
                if (ev.Event.InsertOrRemove &&
                    !CanStripInsertHand(args.User, args.Target.Value, args.Used.Value, ev.Event.SlotOrHandName, entity.Comp) ||
                    !CanStripRemoveHand(args.User, args.Target.Value, args.Used.Value, ev.Event.SlotOrHandName))
                        ev.Cancel();
            }
        }

        private void OnStrippableDoAfterFinished(Entity<HandsComponent> entity, ref StrippableDoAfterEvent ev)
        {
            if (ev.Cancelled)
                return;

            Debug.Assert(entity.Owner == ev.User);
            Debug.Assert(ev.Target != null);
            Debug.Assert(ev.Used != null);
            Debug.Assert(ev.SlotOrHandName != null);

            if (ev.InventoryOrHand)
            {
                if (ev.InsertOrRemove)
                        StripInsertInventory(entity.Owner, ev.Target.Value, ev.Used.Value, ev.SlotOrHandName, entity.Comp);
                else    StripRemoveInventory(entity.Owner, ev.Target.Value, ev.Used.Value, ev.SlotOrHandName, ev.Args.Hidden);
            }
            else
            {
                if (ev.InsertOrRemove)
                        StripInsertHand(entity.Owner, ev.Target.Value, ev.Used.Value, ev.SlotOrHandName, ev.Args.Hidden, entity.Comp);
                else    StripRemoveHand(entity.Owner, ev.Target.Value, ev.Used.Value, ev.Args.Hidden, entity.Comp);
            }
        }
    }
}
