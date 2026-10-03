using System.Windows.Controls;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Views.Pages;

public partial class AccountsPage : UserControl
{
    public AccountsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is AccountsViewModel vm) { vm.PropertyChanged += FormChanged; vm.StopPreviewAsync = Preview.StopAsync; } };
        Unloaded += (_, _) =>
        {
            if (DataContext is AccountsViewModel vm) { vm.PropertyChanged -= FormChanged; vm.CloseAllModals(); }
            SkinPassword.Clear();
        };
    }
    private void SkinPasswordChanged(object sender, RoutedEventArgs e)
    { if (DataContext is AccountsViewModel vm) vm.LittleSkinPassword = SkinPassword.Password; }
    private void FormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AccountsViewModel vm && e.PropertyName == nameof(AccountsViewModel.LittleSkinPassword) && vm.LittleSkinPassword.Length == 0)
            SkinPassword.Clear();
    }
    private void AddAccountClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "离线账户", Command = vm.OpenOfflineModalCommand });
        menu.Items.Add(new MenuItem { Header = "Microsoft", Command = vm.OpenMicrosoftModalCommand });
        menu.Items.Add(new MenuItem { Header = "LittleSkin", Command = vm.OpenLittleSkinModalCommand });
        menu.IsOpen = true;
    }
    private void CardsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 680;
        AccountCards.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : .9, GridUnitType.Star);
        AccountCards.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 20);
        AccountCards.ColumnDefinitions[2].Width = new GridLength(narrow ? 0 : 1.1, GridUnitType.Star);
        Grid.SetColumn(SkinCard, narrow ? 0 : 2); Grid.SetRow(SkinCard, narrow ? 1 : 0);
        SkinCard.Margin = new Thickness(0, narrow ? 20 : 0, 0, 0);
    }
    private void ResetSkinCamera(object sender, RoutedEventArgs e) => Preview.ResetCamera();
    private void AccountClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: AccountProfile account } row || DataContext is not AccountsViewModel vm) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != row)
        {
            if (source is ButtonBase) return;
            source = VisualTreeHelper.GetParent(source);
        }
        row.Focus();
        if (e.ClickCount == 2) vm.SelectAndReturnHome(account); else vm.SelectAccount(account);
        e.Handled = true;
    }
    private void AccountKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { DataContext: AccountProfile account } row || DataContext is not AccountsViewModel vm || !row.IsKeyboardFocused) return;
        if (e.Key == Key.Enter) { vm.SelectAndReturnHome(account); e.Handled = true; }
        else if (e.Key == Key.Space) { vm.SelectAccount(account); e.Handled = true; }
    }
}
