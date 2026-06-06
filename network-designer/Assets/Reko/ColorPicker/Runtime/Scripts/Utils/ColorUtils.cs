using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko.ColorPicker
{
    public static class ColorUtils
    {
        public static Color HSVToRGB(Vector3 hsv)
        {
            return HSVToRGB(hsv, 1.0f);
        }

        public static Color HSVToRGB(Vector3 hsv, float alpha)
        {
            float H = hsv[0];
            float S = hsv[1];
            float V = hsv[2];
            if (S == 0)
                return new Color(V, V, V, 1);

            H = H * 6;
            float C = V * S;
            float X = C * (1 - Mathf.Abs((H % 2) - 1));
            float m = V - C;

            Color rgb = new Color(1, 1, 1, alpha);
            if (H < 1)
                rgb = new Color(C, X, 0, alpha);
            else if (H < 2)
                rgb = new Color(X, C, 0, alpha);
            else if (H < 3)
                rgb = new Color(0, C, X, alpha);
            else if (H < 4)
                rgb = new Color(0, X, C, alpha);
            else if (H < 5)
                rgb = new Color(X, 0, C, alpha);
            else
                rgb = new Color(C, 0, X, alpha);

            rgb += new Color(m, m, m, 0);
            return rgb;
        }

        public static Vector3 RGBToHSV(Color rgb)
        {
            float cMax = Mathf.Max(rgb.r, rgb.g, rgb.b);
            float cMin = Mathf.Min(rgb.r, rgb.g, rgb.b);
            float delta = cMax - cMin;


            var h = new Color(0, 2.0f, 4.0f, 0.0f);
            Color diff = (new Color(rgb.g - rgb.b, rgb.b - rgb.r, rgb.r - rgb.g) / delta + h) / 6.0f;

            Vector3 hsv = new Vector3(0, 0, cMax);
            if (cMax != 0)
            {
                hsv[1] = delta / cMax;
            }

            if (delta == 0)
            {
                hsv[0] = 0;
            }
            else if (rgb.r > rgb.g && rgb.r > rgb.b)
            {
                hsv[0] = diff.r;
            }
            else if (rgb.g > rgb.b)
            {
                hsv[0] = diff.g;
            }
            else
            {
                hsv[0] = diff.b;
            }
            if (hsv[0] < 0)
                hsv[0] += 1.0f;

            return hsv;
        }

        public static string RGBToHex(Color rgb)
        {
            var color32 = (Color32)rgb;
            var values = new[] { color32.r, color32.g, color32.b };
            return "#" + BitConverter.ToString(values).Replace("-", string.Empty);
        }

        public static Color HexToRGB(string hexColor)
        {
            var index = hexColor.StartsWith("#") ? 1 : 0;

            hexColor = hexColor.PadRight(7, ' ');

            var rgb = new Color32(0, 0, 0, 255);
            for (int i = 0; i < 3; i++)
            {
                var i2 = i * 2 + index;
                var v1 = HexValue(hexColor[i2]);
                var v2 = HexValue(hexColor[i2 + 1]);
                rgb[i] = (byte)(v1 * 16 + v2);
            }
            return rgb;
        }

        private static int HexValue(char c)
        {
            var index = "0123456789ABCDEF".IndexOf(char.ToUpper(c));
            if (index < 0) return 0;
            return index;
        }

    }
}