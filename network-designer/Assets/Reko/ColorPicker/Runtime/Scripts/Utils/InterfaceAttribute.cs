using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Reko.ColorPicker
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class InterfaceAttribute : PropertyAttribute
    { 
        public Type RequiredType { get; private set; }

        public InterfaceAttribute(Type type)
        {
            RequiredType = type;
        }
    }
}