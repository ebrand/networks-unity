using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Reko.ColorPicker
{
    [RequireComponent(typeof(RectTransform))]
    public class RadialSlider : Selectable, IDragHandler, IInitializePotentialDragHandler, ISingleInput<float>
    {
        [SerializeField]
        private bool m_reverseDirection;

        [SerializeField]
        private bool m_mirror;

        [SerializeField]
        private RectTransform m_handleRect;

        [SerializeField]
        private UnityEvent<float> m_OnValueChanged = new UnityEvent<float>();

        [SerializeField]
        [Range(0f, 1f)]
        private float m_value;

        private Vector3 m_initialHandlePos;
        private RectTransform m_rect;
        private float m_prevValue;
        private bool m_oppositeMirrorSide = false;

        public UnityEvent<float> onValueChanged => m_OnValueChanged;

        /// <summary>
        /// Set or get a value from 0 to 1
        /// </summary>
        public float Value
        {
            get => m_value;
            set
            {
                if (m_value == value)
                    return;
                UpdateValue(value, true);
            }
        }

        public void SetValueWithoutNotify(float value)
        {
            UpdateValue(value, false);
        }

        protected override void Awake()
        {
            base.Awake();

            m_rect = (RectTransform)transform;
            m_initialHandlePos = m_rect.InverseTransformPoint(m_handleRect.position);
            m_prevValue = m_value;
        }

        private void Update()
        {
#if UNITY_EDITOR
            // to support changes in inspector
            if(m_prevValue != m_value)
            {
                UpdateValue(m_value, true);
            }
#endif
        }


        private void UpdateValue(float value, bool notify)
        {
            m_value = value;
            m_prevValue = m_value;
            UpdateHandlePosition();
            if(notify)
            {
                m_OnValueChanged?.Invoke(m_value);
            }
        }


        public void OnDrag(PointerEventData eventData)
        {
            if (CanDrag(eventData))
            {
                UpdateDragPosition(eventData.position, eventData.pressEventCamera);
            }
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);

            if (!CanDrag(eventData))
                return;

            UpdateDragPosition(eventData.position, eventData.pressEventCamera);
        }

        private bool CanDrag(PointerEventData eventData)
        {
            return IsActive() && IsInteractable() && eventData.button == PointerEventData.InputButton.Left;
        }

        private void UpdateDragPosition(Vector2 position, Camera cam)
        {
            var dir = GetNormalizedDirection(position, cam);

            var angle = CircleUtils.GetAngle(dir);
            var newValue = angle / (Mathf.PI * 2);
            newValue = InvConvert(newValue);

            UpdateValue(newValue, true);
        }

        private void UpdateHandlePosition()
        {
            var rot = -Convert(m_value) * 360.0f;
            m_handleRect.localRotation = Quaternion.Euler(0, 0, rot);
            var localPosition = m_handleRect.InverseTransformPoint(m_rect.TransformPoint(m_initialHandlePos));
            m_handleRect.localPosition = m_handleRect.localRotation * localPosition;
        }

        private Vector2 GetNormalizedDirection(Vector2 screenPos, Camera cam)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(m_rect, screenPos, cam, out Vector2 localPoint);
            var normalized = Rect.PointToNormalized(m_rect.rect, localPoint);
            normalized -= new Vector2(0.5f, 0.5f);
            normalized.Normalize();
            return normalized;
        }


        private float InvConvert(float value)
        {
            if (m_reverseDirection)
                value = 1.0f - value;

            if (!m_mirror)
                return value;

            value = 2 * value;

            m_oppositeMirrorSide = value > 1;
            if (m_oppositeMirrorSide)
                value = 2 - value;
                
            return value;
        }

        private float Convert(float value)
        {
            if (m_mirror)
            {
                value = value / 2;
                if (m_oppositeMirrorSide)
                {
                    value = 1 - value;
                }
            }

            if (m_reverseDirection)
                value = 1.0f - value;

            return value;
        }
    }
}