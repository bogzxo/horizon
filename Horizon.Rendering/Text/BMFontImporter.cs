using Bogz.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

using Horizon.Engine;
using Horizon.HIDL.Lexing;
using Horizon.OpenGL.Assets;

namespace Horizon.Rendering.Text;
/// <summary>
/// Basic BMFont importer, supports only one page and one channel.
/// </summary>
public class BMFontImporter
{
    private class BMParser
    {
        private enum BMToken
        {
            // Primitive Types
            Identifier,
            Number,
            Equals,
            String,
            NewLine,
            EndOfFile,
            Comma,
            Dash,

            // Specific Types
            Char,
            Id,
            Width,
            Height,
            X,
            Y,
            XOffset,
            YOffset,
            XAdvance,
            Page,
            Channel,
        }

        private readonly struct ParserNode(in BMToken type, in string content)
        {
            public readonly BMToken Type { get; init; } = type;
            public readonly string Content { get; init; } = content;
        }


        public static (CharDefinition[] definitions, string imgPath) Parse(in string bmFile)
        {
            if (!File.Exists(bmFile))
            {
                Log.Error($"[BMFont Parser] Couldn't find file '{bmFile}'!");
                return (Array.Empty<CharDefinition>(), string.Empty);
            }
            string[] lines = File.ReadAllLines(bmFile);
            return ParseTokens(Tokenize(lines));
        }



        private static ParserNode Expect(ref Queue<ParserNode> queue, in BMToken type)
        {
            var node = queue.Dequeue();
            if (node.Type != type)
                Log.Error($"[BMFont Parser] Expected token {type} but got {node.Type}!");
            return node;
        }

        private static (CharDefinition[] defs, string filePath) ParseTokens(in ParserNode[] nodes)
        {
            Queue<ParserNode> queue = new(nodes);
            List<CharDefinition> definitions = [];
            string path = string.Empty;

            // Skip beggining of header as we dont really need that here
            while (queue.Peek().Content.CompareTo("file") != 0)
                queue.Dequeue();

            // read file path
            Expect(ref queue, BMToken.Identifier); // Consume file
            Expect(ref queue, BMToken.Equals); // Consume file
            path = Expect(ref queue, BMToken.String).Content; // Consume file path

            // Skip the rest of the header as we dont really need that here
            while (queue.Peek().Content.CompareTo("char") != 0)
                queue.Dequeue();

            static int parseKeyValue(ref Queue<ParserNode> queue, in BMToken type)
            {
                bool negative = false;
                Expect(ref queue, type); // Consume id
                Expect(ref queue, BMToken.Equals); // Consume =

                // handle negative numbers
                if (queue.Peek().Type == BMToken.Dash)
                {
                    Expect(ref queue, BMToken.Dash);
                    negative = true;
                }

                return (negative ? -1 : 1) * int.Parse(Expect(ref queue, BMToken.Number).Content);
            }

            while (queue.Peek().Type != BMToken.EndOfFile)
            {
                var node = queue.Peek();

                if (node.Type == BMToken.Char)
                {
                    Expect(ref queue, BMToken.Char); // Consume char

                    // parse entire line
                    char id = ' ';
                    int xOffset = 0, yOffset = 0, xAdvance = 0, xPos = 0, yPos = 0, width = 0, height = 0;

                    // Parse ID
                    if (queue.Peek().Type == BMToken.Id)
                        id = (char)parseKeyValue(ref queue, BMToken.Id);

                    // Parse X
                    if (queue.Peek().Type == BMToken.X)
                        xPos = parseKeyValue(ref queue, BMToken.X);
                    // Parse Y
                    if (queue.Peek().Type == BMToken.Y)
                        yPos = parseKeyValue(ref queue, BMToken.Y);

                    // Parse width
                    if (queue.Peek().Type == BMToken.Width)
                        width = parseKeyValue(ref queue, BMToken.Width);
                    // Parse height
                    if (queue.Peek().Type == BMToken.Height)
                        height = parseKeyValue(ref queue, BMToken.Height);

                    // Parse xoffset
                    if (queue.Peek().Type == BMToken.XOffset)
                        xOffset = parseKeyValue(ref queue, BMToken.XOffset);
                    // Parse yoffset
                    if (queue.Peek().Type == BMToken.YOffset)
                        yOffset = parseKeyValue(ref queue, BMToken.YOffset);

                    // Parse xadvance
                    if (queue.Peek().Type == BMToken.XAdvance)
                        xAdvance = parseKeyValue(ref queue, BMToken.XAdvance);

                    definitions.Add(new CharDefinition
                    {
                        Id = id,
                        Offset = new Vector2(xOffset, yOffset),
                        Position = new Vector2(xPos, yPos),
                        Size = new Vector2(width, height),
                        XAdvance = xAdvance
                    });

                    // Parse an entire line
                    while (queue.Peek().Type != BMToken.NewLine)
                    {
                        // Discard
                        queue.Dequeue();
                    }
                }
                else queue.Dequeue(); // Throwaway case
            }
            return ([.. definitions], path);
        }

        private static ParserNode[] Tokenize(string[] lines)
        {
            List<ParserNode> tokens = [];

            string parseString(ref Queue<char> queue, in Func<char, bool> condition)
            {
                StringBuilder sb = new();

                while (queue.TryPeek(out char next))
                {
                    if (condition.Invoke(next))
                        sb.Append(queue.Dequeue());
                    else break;
                }

                return sb.ToString();
            }

            StringBuilder stringBuilder = new();
            foreach (string line in lines)
            {
                Queue<char> charQueue = new(line.ToCharArray());

                while (charQueue.Count > 0)
                {
                    char nextChar = charQueue.Peek();

                    // Handle whitespace
                    if (char.IsWhiteSpace(nextChar))
                    {
                        charQueue.Dequeue();
                        continue;
                    }

                    if (nextChar == '"')
                    {
                        // Consume the "
                        charQueue.Dequeue();

                        string content = parseString(ref charQueue, (c) => c != '"');
                        tokens.Add(new(BMToken.String, content));

                        // Consume the "
                        charQueue.Dequeue();
                        continue;
                    }

                    // Handle equals =
                    if (nextChar == '=')
                    {
                        charQueue.Dequeue();
                        tokens.Add(new ParserNode(BMToken.Equals, string.Empty));
                        continue;
                    }
                    // Handle comma ,
                    if (nextChar == ',')
                    {
                        charQueue.Dequeue();
                        tokens.Add(new ParserNode(BMToken.Comma, string.Empty));
                        continue;
                    }
                    // Handle dash -
                    if (nextChar == '-')
                    {
                        charQueue.Dequeue();
                        tokens.Add(new ParserNode(BMToken.Dash, string.Empty));
                        continue;
                    }

                    // Handle identifier and special cases
                    if (char.IsLetter(nextChar))
                    {
                        string content = parseString(ref charQueue, char.IsLetter);

                        // identify special cases
                        tokens.Add(new(content switch
                        {
                            "char" => BMToken.Char,
                            "id" => BMToken.Id,
                            "x" => BMToken.X,
                            "y" => BMToken.Y,
                            "width" => BMToken.Width,
                            "height" => BMToken.Height,
                            "xoffset" => BMToken.XOffset,
                            "yoffset" => BMToken.YOffset,
                            "xadvance" => BMToken.XAdvance,
                            _ => BMToken.Identifier,
                        }, content));
                        continue;
                    }

                    // Handle number
                    if (char.IsNumber(nextChar))
                    {
                        string content = parseString(ref charQueue, char.IsNumber);
                        tokens.Add(new(BMToken.Number, content));
                        continue;
                    }
                }

                tokens.Add(new ParserNode(BMToken.NewLine, string.Empty));
            }
            tokens.Add(new ParserNode(BMToken.EndOfFile, string.Empty));

            return [.. tokens];
        }
    }

    public Texture Texture { get; private set; }
    public Dictionary<char, CharDefinition> Definitions { get; init; }

    /// <summary>
    /// The distance in pixels between the tops of two consecutive lines, 0 if the font doesn't say.
    /// </summary>
    public int LineHeight { get; private set; }

    /// <summary>
    /// The distance in pixels from the top of a line down to its baseline, 0 if the font doesn't say.
    /// </summary>
    public int Base { get; private set; }

    // The char parser skips the header, so the "common" line is picked out separately.
    private void ReadCommon(string bmFile)
    {
        if (!File.Exists(bmFile))
            return;

        foreach (string line in File.ReadLines(bmFile))
        {
            if (!line.StartsWith("common"))
                continue;

            foreach (string pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("lineHeight=") && int.TryParse(pair["lineHeight=".Length..], out int lineHeight))
                    LineHeight = lineHeight;
                else if (pair.StartsWith("base=") && int.TryParse(pair["base=".Length..], out int baseline))
                    Base = baseline;
            }
            return;
        }
    }

    public BMFontImporter(in string dir, in string bmFile)
    {
        ReadCommon(Path.Combine(dir, bmFile));

        (CharDefinition[] defs, string path) = BMParser.Parse(Path.Combine(dir, bmFile));
        if (GameEngine.Instance.ObjectManager.Textures.TryCreateOrGet(
            path,
            new OpenGL.Descriptions.TextureDescription
            {
                Definition = OpenGL.Descriptions.TextureDefinition.RgbaUnsignedByteNearest,
                Paths = [Path.Combine(dir, path)]
            },
            out var result))
        {
            Texture = result.Asset;
        }
        else
        {
            Log.Error(result.Message);
        }

        Definitions = [];
        foreach (var item in defs)
        {
            Definitions.Add(item.Id, item);
        }
    }
}
