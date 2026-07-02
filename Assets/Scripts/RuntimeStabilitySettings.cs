using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RuntimeStabilitySettings : MonoBehaviour
{
    [Header("24/7 Runtime")]
    public int targetFrameRate = 60;
    public bool neverSleep = true;
    public bool disableVSync = true;

    private void Awake()
    {
        if (neverSleep)
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

        QualitySettings.vSyncCount = disableVSync ? 0 : QualitySettings.vSyncCount;
        Application.targetFrameRate = targetFrameRate;
    }
}
