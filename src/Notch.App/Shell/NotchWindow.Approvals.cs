using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Notch.App.Agents;
using Notch.Core.Agents;

namespace Notch.App.Shell;

// Agents' permission prompts and questions, at the top of Home, to answer without going to the agent's window.
public partial class NotchWindow
{
    private const int MaxApprovalCards = 3;

    private ApprovalService? _approvals;

    /// <summary>A prompt came in since the notch was last opened; opening it shows Home, where the prompt is.</summary>
    private bool _approvalArrived;

    /// <summary>Shows the prompts <paramref name="approvals"/> holds, from now on.</summary>
    internal void AttachApprovals(ApprovalService approvals)
    {
        _approvals = approvals;
        approvals.Changed += ShowApprovals;
        approvals.Arrived += _ =>
        {
            _approvalArrived = true;
            if (_expanded)
            {
                ShowNewApprovals();
            }
        };
        ShowApprovals();
    }

    /// <summary>Goes to Home when a prompt is waiting there that the user has not seen, unless they are typing elsewhere.</summary>
    private void ShowNewApprovals()
    {
        if (!_approvalArrived || _approvals is not { Pending.Count: > 0 })
        {
            return;
        }

        _approvalArrived = false;
        if (_tab != NotchTab.Home && !KeyboardInUse)
        {
            TabHome.IsChecked = true;
        }
    }

    private void ShowApprovals()
    {
        ApprovalRows.Children.Clear();
        IReadOnlyList<PendingApproval> pending = _approvals?.Pending ?? [];
        foreach (PendingApproval approval in pending.Take(MaxApprovalCards))
        {
            ApprovalRows.Children.Add(ApprovalCard(approval));
        }

        if (pending.Count > MaxApprovalCards)
        {
            var more = new TextBlock { Text = $"{pending.Count - MaxApprovalCards} more waiting", FontSize = 11, Margin = new Thickness(4, 0, 0, HomeRowGap) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            ApprovalRows.Children.Add(more);
        }

        ApprovalRows.Visibility = ApprovalRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private Border ApprovalCard(PendingApproval approval)
    {
        ApprovalRequest request = approval.Request;
        var stack = new StackPanel();

        // Who asks, and where; the cross leaves the prompt to the agent's own window.
        var head = new DockPanel { LastChildFill = true };
        var dismiss = new Button { Content = "\uE711", Width = 22, Height = 22, FontSize = 10, ToolTip = "Answer it in the agent's own window instead" };
        dismiss.SetResourceReference(StyleProperty, "IconButton");
        dismiss.Click += (_, _) => _approvals?.Dismiss(approval);
        DockPanel.SetDock(dismiss, Dock.Right);
        head.Children.Add(dismiss);
        head.Children.Add(AgentMark(request.AgentId));
        string who = AgentCatalog.ById(request.AgentId)?.DisplayName ?? request.AgentId;
        string? folder = AppSessions.FolderName(request.Folder);
        var label = new TextBlock
        {
            Text = folder is null ? who : $"{who} · {folder}",
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.SetResourceReference(StyleProperty, "CardLabel");
        head.Children.Add(label);
        stack.Children.Add(head);

        var title = new TextBlock { Text = request.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(title);

        if (request.Subject is { } subject)
        {
            var line = new TextBlock { Text = subject, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 64, Margin = new Thickness(0, 4, 0, 0) };
            line.SetResourceReference(TextBlock.FontFamilyProperty, "CodeFontFamily");
            stack.Children.Add(line);
        }

        if (request.Detail is { } detail)
        {
            stack.Children.Add(DetailBox(detail));
        }

        var picks = new Dictionary<string, HashSet<string>>();
        Button? send = null;
        foreach (ApprovalQuestion question in request.Questions)
        {
            stack.Children.Add(QuestionBlock(question, picks, () => send?.SetValue(IsEnabledProperty, AllAnswered(request, picks))));
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        void Add(string text, bool main, Action click)
        {
            var button = new Button { Content = text };
            button.SetResourceReference(StyleProperty, "PillButton");
            if (main)
            {
                button.SetResourceReference(BackgroundProperty, "AccentHoverBrush");
            }

            button.Click += (_, _) => click();
            buttons.Children.Add(button);
            if (text == "Send")
            {
                send = button;
            }
        }

        switch (request.Kind)
        {
            case ApprovalKind.Question:
                Add("Send", true, () => _approvals?.Answer(approval, new ApprovalAnswer(
                    ApprovalDecision.Allow,
                    picks.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value]))));
                send!.IsEnabled = AllAnswered(request, picks);
                break;

            case ApprovalKind.Plan:
                Add("Keep planning", false, () => _approvals?.Answer(approval, new ApprovalAnswer(ApprovalDecision.Deny)));
                Add("Start", true, () => _approvals?.Answer(approval, new ApprovalAnswer(ApprovalDecision.Allow)));
                break;

            default:
                Add("Deny", false, () => _approvals?.Answer(approval, new ApprovalAnswer(ApprovalDecision.Deny)));
                if (request.CanAlwaysAllow)
                {
                    Add("Always allow", false, () => _approvals?.Answer(approval, new ApprovalAnswer(ApprovalDecision.AllowAlways)));
                }

                Add("Allow", true, () => _approvals?.Answer(approval, new ApprovalAnswer(ApprovalDecision.Allow)));
                break;
        }

        stack.Children.Add(buttons);

        var card = new Border { Child = stack, Margin = new Thickness(0, 0, 0, HomeRowGap) };
        card.SetResourceReference(StyleProperty, "Card");
        card.SetResourceReference(Border.BorderBrushProperty, "AttentionBrush");
        return card;
    }

    private static bool AllAnswered(ApprovalRequest request, Dictionary<string, HashSet<string>> picks) =>
        request.Questions.All(q => q.Options.Count == 0 || picks.TryGetValue(q.Text, out HashSet<string>? chosen) && chosen.Count > 0);

    /// <summary>One question with its options as buttons; a chosen one is filled in with the accent.</summary>
    private static StackPanel QuestionBlock(ApprovalQuestion question, Dictionary<string, HashSet<string>> picks, Action changed)
    {
        var block = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (question.Header is { } header)
        {
            var label = new TextBlock { Text = header };
            label.SetResourceReference(StyleProperty, "CardLabel");
            block.Children.Add(label);
        }

        block.Children.Add(new TextBlock { Text = question.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var options = new WrapPanel { Margin = new Thickness(-6, 0, 0, 0) };
        var chosen = picks[question.Text] = [];
        void Show()
        {
            foreach (Button button in options.Children.OfType<Button>())
            {
                button.SetResourceReference(BackgroundProperty, chosen.Contains((string)button.Tag) ? "AccentHoverBrush" : "ControlBrush");
            }
        }

        foreach (ApprovalOption option in question.Options)
        {
            var button = new Button { Content = option.Label, Tag = option.Label, ToolTip = option.Description, Margin = new Thickness(6, 6, 0, 0) };
            button.SetResourceReference(StyleProperty, "PillButton");
            button.Click += (_, _) =>
            {
                // One option, or as many as the question allows; a second click takes a pick back.
                if (!chosen.Remove(option.Label))
                {
                    if (!question.MultiSelect)
                    {
                        chosen.Clear();
                    }

                    chosen.Add(option.Label);
                }

                Show();
                changed();
            };
            options.Children.Add(button);
        }

        block.Children.Add(options);
        return block;
    }

    /// <summary>The change, command description or plan, in a box of its own that scrolls when it is long.</summary>
    private static Border DetailBox(string detail)
    {
        var text = new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "CodeFontFamily");
        var box = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 7, 10, 7),
            Child = new ScrollViewer { Content = text, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
        };
        box.SetResourceReference(Border.BackgroundProperty, "ControlBrush");
        box.SetResourceReference(Border.CornerRadiusProperty, "SmallRadius");
        return box;
    }

    /// <summary>The agent's logo, or its glyph when Notch has no logo for it.</summary>
    private static FrameworkElement AgentMark(string agentId)
    {
        if (Application.Current.TryFindResource("AgentLogo." + agentId) is Geometry logo)
        {
            var path = new Path { Data = logo, Width = 12, Height = 12, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            return path;
        }

        var glyph = new TextBlock { Text = AgentCatalog.ById(agentId)?.Glyph ?? "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return glyph;
    }
}
