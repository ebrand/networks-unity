using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Reko.ColorPicker
{
    [RequireComponent(typeof(RectTransform))]
    public class Slider2D : Selectable, IDragHandler, IInitializePotentialDragHandler
    {
        [SerializeField]
        private RectTransform m_handleRect;

        [SerializeField]
        private UnityEvent<Vector2> m_OnValueChanged = new UnityEvent<Vector2>();

        [SerializeField]
        [Range(0f, 1f)]
        private float m_xValue;
        
        [SerializeField]
        [Range(0f, 1f)]
        private float m_yValue;

        [SerializeField]
        private bool m_usePolarCoordinates = false;

        private RectTransform m_rect;
        private Vector2 m_prevValue;

        public UnityEvent<Vector2> onValueChanged => m_OnValueChanged;

        public float XValue { get => m_xValue; set => UpdateValues(value, m_yValue, true); }
        public float YValue { get => m_yValue; set => UpdateValues(m_xValue, value, true); }

        public void SetXValueWithoutNotify(float x)
        {
            UpdateValues(x, m_yValue, false);
        }
        public void SetYValueWithoutNotify(float y)
        {
            UpdateValues(m_xValue, y, false);
        }

        protected override void Awake()
        {
            base.Awake();

            m_rect = (RectTransform)m_handleRect.parent;
            m_prevValue.x = m_xValue;
            m_prevValue.y = m_yValue;
        }


        protected override void Start()
        {
            base.Start();

            StartCoroutine(InitializeHandlePosition());
        }

        private void Update()
        {
#if UNITY_EDITOR
            // to support changes in inspector
            if (m_prevValue.x != m_xValue || m_prevValue.y != m_yValue)
            {
                UpdateValues(m_xValue, m_yValue, true);
            }
#endif
        }

        private void UpdateValues(float x, float y, bool notify)
        {
            m_prevValue.x = m_xValue = x;
            m_prevValue.y = m_yValue = y;
            UpdateHandlePosition();
            if(notify)
            {
                m_OnValueChanged?.Invoke(m_prevValue);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if(CanDrag(eventData))
            {
                UpdateDragPosition(eventData.position, eventData.pressEventCamera);
            }
        }

        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            if (CanDrag(eventData))
            {
                UpdateDragPosition(eventData.position, eventData.pressEventCamera);
            }
        }

        private bool CanDrag(PointerEventData eventData)
        {
            return IsActive() && IsInteractable() && eventData.button == PointerEventData.InputButton.Left;
        }

        private void UpdateDragPosition(Vector2 position, Camera cam)
        {
            var coords = GetNormalizedPosition(position, cam);
            if (m_usePolarCoordinates)
            {
                coords = 2 * coords - Vector2.one;
                coords = coords / GetAspectRatioFactor();
                var polar = coords.ConvertToPolarCoordinates();
                polar[0] = Mathf.Clamp(polar[0], 0, 1);
                polar[1] = Mathf.Clamp(polar[1], 0, 1);
                coords = polar;
            }
            UpdateValues(coords.x, coords.y, true);
        }

        private IEnumerator InitializeHandlePosition()
        {
            // one frame is need to setup rect transform correctly
            yield return null;
            UpdateHandlePosition();
        }

        private void UpdateHandlePosition()
        {
            var coords = new Vector2(m_xValue, m_yValue);
            if(m_usePolarCoordinates)
            {
                var polar = PolarCoordinates.Create(m_xValue, m_yValue);
                coords = polar.ConvertFromPolarCoordinates();
                coords = coords * GetAspectRatioFactor(); 
                coords = (coords + Vector2.one) * 0.5f;
            }
            var pos = Rect.NormalizedToPoint(m_rect.rect, coords);
            m_handleRect.anchoredPosition = pos;
        }

        private Vector2 GetNormalizedPosition(Vector2 screenPos, Camera cam)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(m_rect, screenPos, cam, out Vector2 localPoint);
            var normalized = Rect.PointToNormalized(m_rect.rect, localPoint);
            return normalized;
        }

        private Vector2 GetAspectRatioFactor()
        {
            var ar = Vector2.one;
            var width = m_rect.rect.width;
            var height = m_rect.rect.height;
            if (width > height)
                ar.x = height / width;
            else
                ar.y = width / height;
            return ar;
        }

    }
}