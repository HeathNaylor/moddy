#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace Moddy.UI
{

    public class SearchBar
    {
        private readonly TextBox _textBox;
        private string _previousText = "";

        public string Text => _textBox.Text ?? "";
        public bool HasChanged { get; private set; }

        public SearchBar(Rectangle bounds)
        {
            _textBox = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null,
                Game1.smallFont,
                Color.Black
            )
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                Text = ""
            };
        }

        public void Update()
        {
            HasChanged = _textBox.Text != _previousText;
            _previousText = _textBox.Text ?? "";
        }

        public void Draw(SpriteBatch b)
        {
            _textBox.Draw(b);

            // Draw placeholder text if empty and not selected
            if (string.IsNullOrEmpty(_textBox.Text) && !_textBox.Selected)
            {
                var placeholder = "Search mods...";
                b.DrawString(
                    Game1.smallFont,
                    placeholder,
                    new Vector2(_textBox.X + 16, _textBox.Y + 8),
                    Color.Gray
                );
            }
        }

        public void ReceiveLeftClick(int x, int y)
        {
            _textBox.Update();
            if (new Rectangle(_textBox.X, _textBox.Y, _textBox.Width, _textBox.Height).Contains(x, y))
                _textBox.SelectMe();
            else
                _textBox.Selected = false;
        }

        public void RecieveTextInput(char c)
        {
            if (_textBox.Selected)
                _textBox.RecieveTextInput(c);
        }

        public void RecieveCommandInput(char c)
        {
            if (_textBox.Selected)
                _textBox.RecieveCommandInput(c);
        }

        public bool Selected
        {
            get => _textBox.Selected;
            set => _textBox.Selected = value;
        }
    }

}
