using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using McLauncher.Core.Auth;

namespace McLauncher.App.Models;

/// <summary>
/// 账号列表里的一行。
/// </summary>
/// <remarks>
/// 只是给 <see cref="MinecraftAccount"/> 套一层界面用的壳：加头像、加"是不是当前选中"
/// 这类纯显示状态。业务数据一概留在 <see cref="Account"/> 里，别在这里复制一份，
/// 否则刷新令牌之后两边就对不上了。
/// </remarks>
public sealed partial class AccountItemView : ObservableObject
{
    public AccountItemView(MinecraftAccount account)
    {
        Account = account;
    }

    /// <summary>真实账号数据（令牌、UUID 都在这）。</summary>
    public MinecraftAccount Account { get; private set; }

    /// <summary>头像。取不到就是 <c>null</c>，界面退化成一个首字母方块。</summary>
    [ObservableProperty]
    private ImageSource? _avatar;

    /// <summary>是不是当前正在用的那个账号。</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string Name => Account.Name;

    public string Uuid => Account.Uuid;

    /// <summary>「微软正版」/「离线」。</summary>
    public string KindLabel => Account.KindLabel;

    public bool IsMicrosoft => Account.IsMicrosoft;

    /// <summary>头像没加载出来时显示的占位字符。</summary>
    public string Initial =>
        string.IsNullOrEmpty(Account.Name) ? "?" : Account.Name[..1].ToUpperInvariant();

    /// <summary>小字说明：微软账号显不显令牌状态，离线账号显示 UUID 前 8 位。</summary>
    public string DetailLabel
    {
        get
        {
            if (!Account.IsMicrosoft) return "离线 · 进不了正版服务器";

            // 从官方启动器导入的账号没有续期凭据，过期后只能回去再导入一次
            if (Account.NeedsReimport)
            {
                return Account.HasUsableToken
                    ? "官方启动器令牌 · 约 24 小时内有效"
                    : "令牌过期了 · 去官方启动器启动一次游戏再导入";
            }

            return Account.HasUsableToken
                ? "登录状态正常"
                : "令牌已过期，启动时会自动刷新";
        }
    }

    /// <summary>令牌刷新完 / 名字变了之后调，把界面重新推一遍。</summary>
    public void Update(MinecraftAccount account)
    {
        Account = account;

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(IsMicrosoft));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(DetailLabel));
        OnPropertyChanged(nameof(Uuid));
    }
}
