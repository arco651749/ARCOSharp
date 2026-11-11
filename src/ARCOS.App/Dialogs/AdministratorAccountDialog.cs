using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ARCOS.App.Services;

namespace ARCOS.App.Dialogs;

/// <summary>
/// SUPERVISOR 登入驗證與帳戶管理視窗
/// </summary>
public static class AdministratorAccountDialog
{
    public static async Task<bool> PromptLoginAsync(Window owner)
    {
        if (!AdministratorAccountManager.Instance.HasCredentialsConfigured())
        {
            return true;
        }

        var tcs = new TaskCompletionSource<bool>();
        var cfg = AdministratorAccountManager.Instance.LoadConfig();

        var win = new Window
        {
            Title = "SUPERVISOR 權限登入驗證 (Administrator Login)",
            Width = 360,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

        var txtUser = new TextBox
        {
            Watermark = "輸入帳號 (Username)",
            Text = cfg.Username,
            Height = 30,
            FontSize = 12
        };

        var txtPass = new TextBox
        {
            Watermark = "輸入密碼 (Password)",
            PasswordChar = '●',
            Height = 30,
            FontSize = 12
        };

        var lblErr = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            IsVisible = false
        };

        var btnLogin = new Button
        {
            Content = "登入 (Login)",
            Width = 100,
            Height = 30,
            Background = new SolidColorBrush(Color.Parse("#0284C7")),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var btnCancel = new Button
        {
            Content = "取消 (Cancel)",
            Width = 90,
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        void TrySubmit()
        {
            if (AdministratorAccountManager.Instance.Verify(txtUser.Text ?? "", txtPass.Text ?? ""))
            {
                tcs.TrySetResult(true);
                win.Close();
            }
            else
            {
                lblErr.Text = "帳號或密碼錯誤，請重新輸入！";
                lblErr.IsVisible = true;
            }
        }

        btnLogin.Click += (_, _) => TrySubmit();
        btnCancel.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            win.Close();
        };

        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                TrySubmit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                tcs.TrySetResult(false);
                win.Close();
                e.Handled = true;
            }
        };

        win.Closed += (_, _) => tcs.TrySetResult(false);

        win.Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "請輸入 Administrator 帳號與密碼以切換至 SUPERVISOR 模式：",
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#0F172A"))
                    },
                    txtUser,
                    txtPass,
                    lblErr,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 6, 0, 0),
                        Children = { btnCancel, btnLogin }
                    }
                }
            }
        };

        await win.ShowDialog(owner);
        return await tcs.Task;
    }

    public static async Task OpenAccountManagerAsync(Window owner)
    {
        var cfg = AdministratorAccountManager.Instance.LoadConfig();

        var win = new Window
        {
            Title = "帳戶管理器 (Administrator Account Manager)",
            Width = 390,
            Height = 265,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#F8FAFC"))
        };

        var txtUser = new TextBox
        {
            Watermark = "管理員帳號 (例如 admin，留空代表不設密碼自由切換)",
            Text = cfg.Username,
            Height = 30,
            FontSize = 12
        };

        var txtPass = new TextBox
        {
            Watermark = "管理員密碼 (留空代表不設密碼自由切換)",
            Text = cfg.Password,
            PasswordChar = '●',
            Height = 30,
            FontSize = 12
        };

        var lblStatus = new TextBlock
        {
            Text = AdministratorAccountManager.Instance.HasCredentialsConfigured()
                ? "目前狀態：已啟用密碼保護 (Administrator.json)"
                : "目前狀態：未設定密碼 (允許自由切換 OPERATOR / SUPERVISOR)",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#0284C7")),
            FontWeight = FontWeight.SemiBold
        };

        var btnSave = new Button
        {
            Content = "儲存設定 (Save)",
            Width = 115,
            Height = 30,
            Background = new SolidColorBrush(Color.Parse("#0284C7")),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var btnClear = new Button
        {
            Content = "清除密碼 (自由切換)",
            Width = 135,
            Height = 30,
            Background = new SolidColorBrush(Color.Parse("#FEF2F2")),
            Foreground = new SolidColorBrush(Color.Parse("#DC2626")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FECACA")),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        var btnClose = new Button
        {
            Content = "關閉",
            Width = 75,
            Height = 30,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        btnSave.Click += (_, _) =>
        {
            AdministratorAccountManager.Instance.SaveConfig(txtUser.Text ?? "", txtPass.Text ?? "");
            win.Close();
        };

        btnClear.Click += (_, _) =>
        {
            AdministratorAccountManager.Instance.SaveConfig("", "");
            win.Close();
        };

        btnClose.Click += (_, _) => win.Close();

        win.Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "設定切換至 SUPERVISOR 所需的帳號與密碼：",
                        FontSize = 12,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#0F172A"))
                    },
                    lblStatus,
                    new TextBlock { Text = "帳號 (Username):", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#475569")) },
                    txtUser,
                    new TextBlock { Text = "密碼 (Password):", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#475569")) },
                    txtPass,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 6, 0, 0),
                        Children = { btnClear, btnClose, btnSave }
                    }
                }
            }
        };

        await win.ShowDialog(owner);
    }
}
