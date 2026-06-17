using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace Reko
{
    public class LongPressEventTrigger : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        [ Tooltip( "How long must pointer be down to trigger a long press" ) ]
        public float m_initialTimeInSeconds = 0.6f;
        [ Tooltip( "In which interval is the event triggered while the pointer is down" ) ]
        public float m_repeatTimeInSeconds = 0.08f;

        public UnityEvent onLongPress = new UnityEvent( );

        public void OnPointerDown( PointerEventData eventData ) 
        {
            InvokeRepeating(nameof(RaiseLongPress), m_initialTimeInSeconds, m_repeatTimeInSeconds);
        }

        public void OnPointerUp( PointerEventData eventData )
        {
            CancelInvoke(nameof(RaiseLongPress));
        }


        public void OnPointerExit( PointerEventData eventData )
        {
            CancelInvoke(nameof(RaiseLongPress));
        }
        
        private void RaiseLongPress()
        {
            onLongPress.Invoke();
        }
    }
}