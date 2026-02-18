#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Moddy.UI;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace Moddy.Patches
{

    public static class TitleMenuPatch
    {
        private const string ModsButtonName = "Mods";
        private const int ModsButtonID = 81200;

        private static readonly System.Reflection.FieldInfo? ButtonsToShowField =
            AccessTools.Field(typeof(TitleMenu), "buttonsToShow");

        // Where we blit our sprite onto the titleButtonsTexture
        private const int BlitX = 296;
        private const int BlitY = 187;
        private static bool _blittedSprite;

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(TitleMenu), nameof(TitleMenu.setUpIcons)),
                postfix: new HarmonyMethod(typeof(TitleMenuPatch), nameof(SetUpIcons_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TitleMenu), nameof(TitleMenu.receiveLeftClick)),
                postfix: new HarmonyMethod(typeof(TitleMenuPatch), nameof(ReceiveLeftClick_Postfix))
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(TitleMenu), nameof(TitleMenu.update)),
                postfix: new HarmonyMethod(typeof(TitleMenuPatch), nameof(Update_Postfix))
            );
        }

        private static bool BlitSpriteOntoTitleTexture(Texture2D titleTex)
        {
            if (_blittedSprite)
                return true;

            try
            {
                var path = Path.Combine(ModEntry.ModDirectoryPath, "assets", "ModsButton.png");
                if (!File.Exists(path))
                {
                    ModEntry.Logger.Log($"ModsButton.png not found at {path}", LogLevel.Warn);
                    return false;
                }

                // Read the game's titleButtonsTexture pixel data
                int texW = titleTex.Width;
                int texH = titleTex.Height;
                var titleData = new Color[texW * texH];
                titleTex.GetData(titleData);

                // Load our sprite PNG
                using var stream = File.OpenRead(path);
                var rawTex = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
                var pngData = new Color[rawTex.Width * rawTex.Height];
                rawTex.GetData(pngData);
                int pngW = rawTex.Width;
                int pngH = rawTex.Height;
                rawTex.Dispose();

                // Blit the sprite at y=187 only. We prevent TitleMenu from
                // swapping sourceRect.Y to 245 (hover) in Update_Postfix,
                // since y=245 overlaps with the Back button sprite.
                int frameH = Math.Min(58, pngH);
                int frameW = Math.Min(74, pngW);
                for (int y = 0; y < frameH; y++)
                {
                    for (int x = 0; x < frameW; x++)
                    {
                        int destIdx = (BlitY + y) * texW + BlitX + x;
                        int srcIdx = y * pngW + x;
                        if (destIdx < titleData.Length && srcIdx < pngData.Length)
                            titleData[destIdx] = pngData[srcIdx];
                    }
                }

                titleTex.SetData(titleData);
                _blittedSprite = true;
                ModEntry.Logger.Log("Blitted Mods sprite onto titleButtonsTexture", LogLevel.Trace);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Failed to blit sprite: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        public static void SetUpIcons_Postfix(TitleMenu __instance)
        {
            try
            {
                var buttons = __instance.buttons;
                if (buttons == null || buttons.Count == 0)
                    return;

                // Remove any previously added Mods button (setUpIcons can be called multiple times)
                buttons.RemoveAll(b => b.name == ModsButtonName);

                var texture = __instance.titleButtonsTexture;
                Rectangle sourceRect;

                // Always re-blit when setUpIcons is called, since the
                // texture is recreated when returning to the title menu
                _blittedSprite = false;
                if (BlitSpriteOntoTitleTexture(texture))
                {
                    sourceRect = new Rectangle(BlitX, BlitY, 74, 58);
                }
                else
                {
                    // Fallback: reuse Co-op sprite
                    sourceRect = new Rectangle(148, 187, 74, 58);
                }

                var modsButton = new ClickableTextureComponent(
                    ModsButtonName,
                    new Rectangle(0, 0, 222, 174), // recalculated below
                    null,
                    "",
                    texture,
                    sourceRect,
                    3f
                )
                {
                    myID = ModsButtonID,
                    visible = true
                };

                // Insert before Exit (last button)
                buttons.Insert(buttons.Count - 1, modsButton);

                // Recalculate all positions for the new count
                RecalculateButtonPositions(__instance, buttons);

                ModEntry.Logger.Log($"Added Mods button (total: {buttons.Count})", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Error in SetUpIcons_Postfix: {ex.Message}", LogLevel.Error);
            }
        }

        private static void RecalculateButtonPositions(TitleMenu menu, List<ClickableTextureComponent> buttons)
        {
            int count = buttons.Count;
            int btnPixelWidth = 74;
            int gapPixels = 8;
            float scale = 3f;
            int scaledWidth = (int)(btnPixelWidth * scale);   // 222
            int scaledGap = (int)(gapPixels * scale);          // 24
            int totalWidth = count * scaledWidth + (count - 1) * scaledGap;
            int startX = menu.width / 2 - totalWidth / 2;
            int btnY = menu.height - 174 - 24;

            for (int i = 0; i < count; i++)
            {
                buttons[i].bounds = new Rectangle(
                    startX + i * (scaledWidth + scaledGap),
                    btnY,
                    scaledWidth,
                    174
                );
            }

            // Fix gamepad neighbor IDs
            for (int i = 0; i < count; i++)
            {
                buttons[i].leftNeighborID = i > 0 ? buttons[i - 1].myID : -1;
                buttons[i].rightNeighborID = i < count - 1 ? buttons[i + 1].myID : -1;
            }
        }

        public static void Update_Postfix(TitleMenu __instance)
        {
            try
            {
                if (TitleMenu.subMenu != null || ButtonsToShowField == null)
                    return;

                int shown = (int)ButtonsToShowField.GetValue(__instance)!;
                int count = __instance.buttons.Count;

                // The reveal animation increments buttonsToShow up to the original count (4).
                // Once it reaches count-1, bump it to include our added button.
                if (shown >= count - 1 && shown < count)
                {
                    ButtonsToShowField.SetValue(__instance, count);
                }

                // TitleMenu swaps button sourceRect.Y between 187 (normal) and 245 (hover).
                // Our sprite is only blitted at y=187, so keep it pinned there.
                foreach (var button in __instance.buttons)
                {
                    if (button.name == ModsButtonName)
                    {
                        button.sourceRect.Y = BlitY;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Error in Update_Postfix: {ex.Message}", LogLevel.Error);
            }
        }

        public static void ReceiveLeftClick_Postfix(TitleMenu __instance, int x, int y)
        {
            try
            {
                if (TitleMenu.subMenu != null)
                    return;

                foreach (var button in __instance.buttons)
                {
                    if (button.name == ModsButtonName && button.containsPoint(x, y))
                    {
                        Game1.playSound("select");
                        TitleMenu.subMenu = new ModBrowserMenu();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.Logger.Log($"Error in ReceiveLeftClick_Postfix: {ex.Message}", LogLevel.Error);
            }
        }
    }

}
