using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Obsync.App.ViewModels;
using Obsync.Shared.Models;

namespace Obsync.App.Views;

public partial class ScriptDiffWindow : Window
{
    private ScrollViewer? _oldScroll;
    private ScrollViewer? _newScroll;
    private bool _syncingScroll;

    public ScriptDiffWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Opens the script &amp; diff viewer for a run as a modal dialog, preselecting
    /// <paramref name="preselect"/> (or the first change).
    /// </summary>
    public static async Task ShowDialogAsync(
        Window? owner, SyncRun run, IReadOnlyList<ObjectChange> changes, GitRepositoryProfile? repository,
        ObjectChange? preselect = null)
    {
        var viewModel = App.Services.GetRequiredService<ScriptDiffViewModel>();
        await viewModel.LoadAsync(run, changes, repository, preselect);

        var window = new ScriptDiffWindow { DataContext = viewModel, Owner = owner };
        window.ShowDialog();
    }

    // Find-in-script raises FindCurrentRow; selecting + scrolling a virtualized ListBox is view
    // work, so the window listens rather than the view model touching controls.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ScriptDiffViewModel old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is ScriptDiffViewModel next)
        {
            next.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptDiffViewModel.FindCurrentRow)
            || DataContext is not ScriptDiffViewModel viewModel
            || viewModel.FindCurrentRow is not { } row)
        {
            return;
        }

        // The searched rows belong to the new-side pane in split view, the single pane otherwise.
        var pane = viewModel.ShowSplit ? NewPane : SinglePane;
        pane.SelectedItem = row;
        pane.ScrollIntoView(row);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WPF has no built-in linked scrolling, so mirror offsets between the split panes. Vertical
        // offsets are item-based (CanContentScroll) and the side-by-side rows are padded to equal
        // counts, so the panes stay line-for-line aligned.
        _oldScroll = FindScrollViewer(OldPane);
        _newScroll = FindScrollViewer(NewPane);
        if (_oldScroll is not null)
        {
            _oldScroll.ScrollChanged += OnPaneScrollChanged;
        }

        if (_newScroll is not null)
        {
            _newScroll.ScrollChanged += OnPaneScrollChanged;
        }
    }

    private void OnPaneScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || (e.VerticalChange == 0 && e.HorizontalChange == 0))
        {
            return;
        }

        var source = (ScrollViewer)sender;
        var target = ReferenceEquals(source, _oldScroll) ? _newScroll : _oldScroll;
        if (target is null)
        {
            return;
        }

        _syncingScroll = true;
        try
        {
            target.ScrollToVerticalOffset(source.VerticalOffset);
            target.ScrollToHorizontalOffset(source.HorizontalOffset);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
