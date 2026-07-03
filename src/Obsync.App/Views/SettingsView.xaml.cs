using System;
using System.Windows;
using System.Windows.Controls;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _subscribed;

    public SettingsView()
    {
        InitializeComponent();

        // Subscribe on Loaded / unsubscribe on Unloaded so the singleton view model does not retain
        // this recreated-per-navigation view (and its proxy-password PasswordBox) after navigating away.
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
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unsubscribe();

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.ProxyPasswordShouldClear -= OnProxyPasswordShouldClear;
            _subscribed = null;
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
}
