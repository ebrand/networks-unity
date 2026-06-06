using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Reko.Editor
{
    [CustomEditor(typeof(UIRoundedRect))]
    public class UIRoundedRectEditor : UnityEditor.Editor
    {
        // properties from Graphic
        private SerializedProperty m_Color;
        private SerializedProperty m_RaycastTarget;
        private SerializedProperty m_RaycastPadding;

        // properties from UIRoundedRect
        private SerializedProperty m_uniformValues;
        private SerializedProperty m_radius;
        private SerializedProperty m_radii;
        private SerializedProperty m_border;
        private SerializedProperty m_borderWidth;
        private SerializedProperty m_shader;



        private void OnEnable()
        {
            m_Color = serializedObject.FindProperty("m_Color");
            m_RaycastTarget = serializedObject.FindProperty("m_RaycastTarget");
            m_RaycastPadding = serializedObject.FindProperty("m_RaycastPadding");

            m_uniformValues = serializedObject.FindProperty("m_uniformValues");
            m_radius = serializedObject.FindProperty("m_radius");
            m_radii = serializedObject.FindProperty("m_radii");
            m_border = serializedObject.FindProperty("m_border");
            m_borderWidth = serializedObject.FindProperty("m_borderWidth");

            m_shader = serializedObject.FindProperty("m_shader");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            AssignShader();

            EditorGUILayout.PropertyField(m_Color);
            EditorGUILayout.PropertyField(m_RaycastTarget);
            EditorGUILayout.PropertyField(m_RaycastPadding);

            EditorGUILayout.PropertyField(m_uniformValues);
            serializedObject.ApplyModifiedProperties();

            if (m_uniformValues.boolValue)
            {
                EditorGUILayout.PropertyField(m_radius);
            }
            else
            {
                EditorGUILayout.PropertyField(m_radii);
            }

            EditorGUILayout.PropertyField(m_border);
            if (m_border.boolValue)
            {
                EditorGUILayout.PropertyField(m_borderWidth);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void AssignShader()
        {
            if (m_shader.objectReferenceValue == null)
            {
                m_shader.objectReferenceValue = Shader.Find(UIRoundedRect.ShaderPath);
            }
        }
    }
}
