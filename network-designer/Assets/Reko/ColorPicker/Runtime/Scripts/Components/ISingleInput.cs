using UnityEngine.Events;

namespace Reko.ColorPicker
{
    public interface ISingleInput<T>
    {
        T Value { get; set; }

        UnityEvent<T> onValueChanged { get; }

        void SetValueWithoutNotify(T value);
    }

}