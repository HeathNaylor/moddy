#nullable enable
using Microsoft.Xna.Framework;

namespace Moddy.UI
{

    public static class UIConstants
    {
        // Panel dimensions
        public const int PanelPadding = 16;
        public const int RowHeight = 80;
        public const int ItemsPerPage = 8;

        // List/detail split
        public const float ListWidthRatio = 0.58f;
        public const float DetailWidthRatio = 0.42f;

        // Colors
        public static readonly Color PanelBackground = new(46, 46, 46);
        public static readonly Color RowNormal = new(60, 60, 60);
        public static readonly Color RowSelected = new(80, 80, 120);
        public static readonly Color RowHover = new(70, 70, 80);
        public static readonly Color TextWhite = Color.White;
        public static readonly Color TextGray = Color.LightGray;
        public static readonly Color TextGold = new(255, 215, 0);
        public static readonly Color BadgeInstalled = new(50, 180, 50);
        public static readonly Color BadgeUpdate = new(230, 160, 30);
        public static readonly Color ButtonInstall = new(60, 160, 60);
        public static readonly Color ButtonUninstall = new(180, 50, 50);
        public static readonly Color ButtonUpdate = new(50, 130, 200);
        public static readonly Color BadgeNexus = new(218, 140, 38);
        public static readonly Color TabActive = new(100, 100, 140);
        public static readonly Color TabInactive = new(60, 60, 60);
        public static readonly Color ButtonBrowse = new(218, 140, 38);
        public static readonly Color BannerPending = new(140, 100, 20);
        public static readonly Color ButtonRestart = new(200, 140, 30);
    }

}
