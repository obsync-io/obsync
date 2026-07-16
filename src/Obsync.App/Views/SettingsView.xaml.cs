using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class SettingsView : UserControl
{
    private static readonly Regex NonDigit = new(@"[^0-9]", RegexOptions.Compiled);

    private SettingsViewModel? _subscribed;

    public SettingsView()
    {
        InitializeComponent();

        // Subscribe on Loaded / unsubscribe on Unloaded so the singleton view model does not retain
        // this recreated-per-navigation view (and its password PasswordBoxes) after navigating away.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        if (DataContext is SettingsViewModel viewModel)
        {
            _subscribed = viewModel;
            viewModel.ProxyPasswordShouldClear += OnProxyPasswordShouldClear;
            viewModel.SmtpPasswordShouldClear += OnSmtpPasswordShouldClear;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.ProxyPasswordShouldClear -= OnProxyPasswordShouldClear;
            _subscribed.SmtpPasswordShouldClear -= OnSmtpPasswordShouldClear;
            _subscribed = null;
        }
    }

    // Lazy per-tab loads: sizes, logs, and support info are only gathered when their tab is opened,
    // so navigating to Settings never pays for a workspace walk or a log-file read up front.
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsTabs) || DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        if (NetworkTab.IsSelected)
        {
            _ = viewModel.EnsureStorageAsync();
        }
        else if (DiagnosticsTab.IsSelected)
        {
            _ = viewModel.EnsureLogsLoadedAsync();
        }
        else if (AboutTab.IsSelected)
        {
            _ = viewModel.EnsureSupportInfoAsync();
        }
    }

    private void OnProxyPasswordShouldClear(object? sender, EventArgs e) => ProxyPasswordBox.Clear();

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.ProxyPassword = box.Password;
        }
    }

    private void OnSmtpPasswordShouldClear(object? sender, EventArgs e) => SmtpPasswordBox.Clear();

    private void OnSmtpPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && sender is PasswordBox box)
        {
            viewModel.SmtpPassword = box.Password;
        }
    }

    // Reject non-numeric typing/paste for the SMTP port so invalid text is never silently dropped.
    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = NonDigit.IsMatch(e.Text);

    private void OnPasteDigitsOnly(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is string text && NonDigit.IsMatch(text))
        {
            e.CancelCommand();
        }
    }
}
