using UnityEngine;

namespace BepInEx.Unity.IL2CPP.Threading;

internal class Il2CppSynchronizationContextRunner : MonoBehaviour
{
    public void Update()
    {
        Il2CppSynchronizationContext.ExecuteTasks();
    }
}
