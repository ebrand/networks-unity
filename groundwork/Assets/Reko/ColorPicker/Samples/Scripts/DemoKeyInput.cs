using UnityEngine;
#if !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

namespace Reko.ColorPicker
{
    public class DemoKeyInput : MonoBehaviour
    {
        [SerializeField] 
        private Demo m_demo;
        
        void Update()
        {
            if(PrevKeyWasPressed())
                m_demo.Previous();
            else if(NextKeyWasPressed())
                m_demo.Next();
        }

        bool PrevKeyWasPressed()
        {
#if !ENABLE_LEGACY_INPUT_MANAGER
            return Keyboard.current[Key.LeftArrow].wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.LeftArrow);
#endif
        }
        
        bool NextKeyWasPressed()
        {
#if !ENABLE_LEGACY_INPUT_MANAGER
            return Keyboard.current[Key.RightArrow].wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.RightArrow);
#endif
        }
    }

}
