using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


namespace Reko.ColorPicker.Editor
{
    [CustomEditor(typeof(ColorPalette))]
    public class ColorPaletteEditor : UnityEditor.Editor
    {
        private SerializedProperty m_colors;
        private SerializedProperty m_items;
        private SerializedProperty m_container;
        private SerializedProperty m_itemPrefab;
        private SerializedProperty m_colorSet;
        private ColorPalette m_colorPalette;
        
        private void OnEnable()
        {
            m_colorPalette = (ColorPalette)target;
            m_colors = serializedObject.FindProperty("m_colors");
            m_items = serializedObject.FindProperty("m_items");
            m_container = serializedObject.FindProperty("m_container");
            m_itemPrefab = serializedObject.FindProperty("m_itemPrefab");
            m_colorSet = serializedObject.FindProperty("m_colorSet");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_colorSet);
            if (EditorGUI.EndChangeCheck())
            {
                var colorSet = m_colorSet.objectReferenceValue as ColorSet;
                AssignColorSet(colorSet.colors);
            }
            
            
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_colors);
            EditorGUILayout.PropertyField(m_itemPrefab);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.SetCurrentGroupName("Change Color Palette");
                int undoGroup = Undo.GetCurrentGroup();

                if (serializedObject.ApplyModifiedProperties() )
                {

                    m_colorSet.objectReferenceValue = null;
                    UpdatePaletteItems();
                }
                Undo.CollapseUndoOperations(undoGroup);
            }
            
        }

        private void UpdatePaletteItems()
        {
            // each palette item is inside an item container
            // step 1: destroy old palette items. hide them if they are part of a prefab and can't be destroyed
            var itemContainers = new List<Transform>();
            for (int i = 0; i < m_items.arraySize; i++)
            {
                var item = m_items.GetArrayElementAtIndex(i).objectReferenceValue as ColorPaletteItem;
                if (item != null)
                {
                    var itemContainer = item.transform.parent;
                    try
                    {
                        Undo.DestroyObjectImmediate(item.gameObject);
                        itemContainers.Add(itemContainer);
                    }
                    catch
                    {
                        // exception -> object is probably part of a prefab, just hide it
                        Undo.RecordObject(itemContainer.gameObject, "Hide item container");
                        itemContainer.gameObject.SetActive(false);
                    }
                }
            }

            // step 2: create new palette items. assign them to existing item containers if possible.
            m_items.ClearArray();
            m_items.arraySize = 0;
            if (m_itemPrefab.objectReferenceValue != null && m_container.objectReferenceValue != null)
            {
                var container = (Transform)m_container.objectReferenceValue;
                m_items.arraySize = m_colors.arraySize;

                for (int i = 0; i < m_colors.arraySize; i++)
                {
                    Transform itemContainer;
                    if (i < itemContainers.Count)
                    {
                        itemContainer = itemContainers[i];
                    }
                    else
                    {
                        itemContainer = new GameObject("Item", typeof(RectTransform)).transform;
                        itemContainer.SetParent(container, false);
                        Undo.RegisterCreatedObjectUndo(itemContainer.gameObject, "Created item container");
                    }

                    var item = (ColorPaletteItem)PrefabUtility.InstantiatePrefab(m_itemPrefab.objectReferenceValue, itemContainer.transform);
                    item.Index = i;
                    item.IsSelected = false;
                    item.Color = m_colors.GetArrayElementAtIndex(i).colorValue;
                    item.Palette = m_colorPalette;
                    var itemProp = m_items.GetArrayElementAtIndex(i);
                    itemProp.objectReferenceValue = item;
                    Undo.RegisterCreatedObjectUndo(item, "Instantiated item");
                }
            }
            
            // part 3: destroy or hide remaining item containers
            for(var i = m_items.arraySize; i < itemContainers.Count; i++)
            {
                try
                {
                    Undo.DestroyObjectImmediate(itemContainers[i].gameObject);
                }
                catch
                {
                    Undo.RecordObject(itemContainers[i].gameObject, "Hide item container");
                    itemContainers[i].gameObject.SetActive(false);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void AssignColorSet(Color[] set)
        {
            Undo.SetCurrentGroupName("Assign Color Set");
            int undoGroup = Undo.GetCurrentGroup();
            
            m_colors.arraySize = set.Length;
            for (int i = 0; i < set.Length; i++)
            {
                var element = m_colors.GetArrayElementAtIndex(i);
                element.colorValue = set[i];
            }

            serializedObject.ApplyModifiedProperties();
            UpdatePaletteItems();
            
            Undo.CollapseUndoOperations(undoGroup);
        }
    }
}