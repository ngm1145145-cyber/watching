using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Watching.Common;
using PasswordBox = System.Windows.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace Watching.Server;

public enum PasswordDialogMode
{
    /// <summary>校验已有密码。</summary>
    Verify,
    /// <summary>设置新密码（输入两次）。</summary>
    Create,
    /// <summary>修改密码（先验旧、再输两次新）。</summary>
    Change
}

public partial class PasswordDialog : Window
{
    private readonly PasswordDialogMode _mode;
    private PasswordBox _old;
    private TextBlock _oldLabel;

    public string Password { get; private set; }

    public PasswordDialog(PasswordDialogMode mode, string title = null)
    {
        InitializeComponent();
        _mode = mode;
        Title = "Watching - " + (title ?? "密码");

        if (mode == PasswordDialogMode.Verify)
        {
            TitleText.Text = title ?? "输入密码";
            HintText.Text = "进入设置需要验证密码。";
        }
        else if (mode == PasswordDialogMode.Create)
        {
            TitleText.Text = "设置密码";
            HintText.Text = "请设置一个密码。设置后，每次进入设置或开启远程控制都需要输入它。";
            Box2.Visibility = Visibility.Visible;
        }
        else
        {
            TitleText.Text = "修改密码";
            HintText.Text = "先输入当前密码，再输入两遍新密码。";

            _oldLabel = new TextBlock
            {
                Text = "当前密码",
                Margin = new Thickness(0, 14, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("FgDim")
            };
            var parent = (Grid)Content;
            parent.Children.Add(_oldLabel);
            Grid.SetRow(_oldLabel, 2);

            _old = new PasswordBox { Height = 34, Margin = new Thickness(0, 22, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
            parent.Children.Add(_old);
            Grid.SetRow(_old, 2);

            Box1.Margin = new Thickness(0, 32, 0, 0);
            Box2.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            if (_old != null) _old.Focus();
            else Box1.Focus();
        };
    }

    private void Box1_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (_mode == PasswordDialogMode.Verify)
        {
            if (!PasswordGate.Verify(Box1.Password))
            {
                ErrorText.Text = "密码不正确，请重试。";
                Box1.SelectAll();
                Box1.Focus();
                return;
            }
            Password = Box1.Password;
            DialogResult = true;
            Close();
            return;
        }

        if (_mode == PasswordDialogMode.Change && !PasswordGate.Verify(_old?.Password ?? ""))
        {
            ErrorText.Text = "当前密码不正确。";
            _old?.SelectAll();
            _old?.Focus();
            return;
        }

        if (Box1.Password.Length < 4)
        {
            ErrorText.Text = "密码至少 4 位。";
            Box1.Focus();
            return;
        }

        if (Box1.Password != Box2.Password)
        {
            ErrorText.Text = "两次输入的密码不一致。";
            Box2.SelectAll();
            Box2.Focus();
            return;
        }

        Password = Box1.Password;
        DialogResult = true;
        Close();
    }
}
