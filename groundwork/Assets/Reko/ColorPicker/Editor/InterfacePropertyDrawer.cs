using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Reko.ColorPicker.Editor
{
    [CustomPropertyDrawer(typeof(InterfaceAttribute))]
    public class InterfacePropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.serializedObject.isEditingMultipleObjects) return;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return;

            var interfaceAttribute = this.attribute as InterfaceAttribute;
            var requiredType = interfaceAttribute.RequiredType;

            EditorGUI.BeginProperty(position, label, property);


            UnityEngine.Object oldReference = property.objectReferenceValue;
            GameObject tempGo = null;
            if (Event.current.type == EventType.Repaint && oldReference == null)
            {
                tempGo = new GameObject("None" + " (" + requiredType.Name + ")");
                tempGo.hideFlags = HideFlags.HideAndDontSave;
                oldReference = tempGo;
            }

            var reference = EditorGUI.ObjectField(position, label, oldReference, typeof(UnityEngine.Object), true);
            if (reference is GameObject go)
            {
                reference = go.GetComponent(requiredType);
            }

            property.objectReferenceValue = reference;

            if(tempGo != null)
            {
                GameObject.DestroyImmediate(tempGo);
            }

            EditorGUI.EndProperty();
        }
    }
}
