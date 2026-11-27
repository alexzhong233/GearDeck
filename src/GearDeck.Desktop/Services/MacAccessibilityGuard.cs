using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;

namespace GearDeck.Desktop.Services;

/// <summary>
/// 规避 FluentAvalonia 2.4.1 中 ItemsRepeaterAutomationPeer.GetChildrenCore 在 macOS
/// NSAccessibility 查询期间触发 NullReferenceException 并导致 SIGABRT 闪退的缺陷。
/// </summary>
public static class MacAccessibilityGuard
{
    private static int _initialized;
    private static readonly FieldInfo? AutomationPeerField = typeof(Control).GetField(
        "_automationPeer",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ItemsRepeater>(
            (r, _) => InstallSafePeer(r), handledEventsToo: true);
        Control.LoadedEvent.AddClassHandler<ItemsRepeater>(
            (r, _) => InstallSafePeer(r), handledEventsToo: true);
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<NavigationView>(
            (nav, _) => PatchVisualSubtree(nav), handledEventsToo: true);
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<NavigationViewItem>(
            (item, _) => PatchVisualSubtree(item), handledEventsToo: true);
        Control.LoadedEvent.AddClassHandler<NavigationViewItem>(
            (item, _) => PatchVisualSubtree(item), handledEventsToo: true);
    }

    public static void InstallSafePeer(ItemsRepeater? repeater)
    {
        if (repeater == null || AutomationPeerField == null)
            return;
        try
        {
            if (AutomationPeerField.GetValue(repeater) is not SafeItemsRepeaterAutomationPeer)
                AutomationPeerField.SetValue(repeater, new SafeItemsRepeaterAutomationPeer(repeater));
        }
        catch
        {
        }
    }

    public static void PatchVisualSubtree(Visual? root)
    {
        if (root == null) return;
        try
        {
            if (root is ItemsRepeater self)
                InstallSafePeer(self);
            foreach (var repeater in root.GetVisualDescendants().OfType<ItemsRepeater>())
                InstallSafePeer(repeater);
        }
        catch
        {
        }
    }

    public sealed class SafeItemsRepeaterAutomationPeer : ControlAutomationPeer
    {
        public SafeItemsRepeaterAutomationPeer(ItemsRepeater owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Group;

        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore()
        {
            try
            {
                return base.GetOrCreateChildrenCore() ?? Array.Empty<AutomationPeer>();
            }
            catch
            {
                return Array.Empty<AutomationPeer>();
            }
        }

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
        {
            try
            {
                return base.GetChildrenCore() ?? Array.Empty<AutomationPeer>();
            }
            catch
            {
                return Array.Empty<AutomationPeer>();
            }
        }
    }

    public sealed class SafeWindowAutomationPeer : WindowAutomationPeer
    {
        public SafeWindowAutomationPeer(Window owner) : base(owner) { }

        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore()
        {
            PatchVisualSubtree(Owner);
            try
            {
                return base.GetOrCreateChildrenCore() ?? Array.Empty<AutomationPeer>();
            }
            catch
            {
                return Array.Empty<AutomationPeer>();
            }
        }

        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
        {
            PatchVisualSubtree(Owner);
            try
            {
                return base.GetChildrenCore() ?? Array.Empty<AutomationPeer>();
            }
            catch
            {
                return Array.Empty<AutomationPeer>();
            }
        }
    }
}
