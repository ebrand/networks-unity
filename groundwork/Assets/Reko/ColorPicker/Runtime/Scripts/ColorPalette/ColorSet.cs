using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko.ColorPicker
{
    [CreateAssetMenu(fileName = "Data", menuName = "ColorPicker/Color Set", order = 1)]
    public class ColorSet : ScriptableObject
    {
        public string description;
        public Color[] colors;
    }
}
