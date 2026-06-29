using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ProjectBootstrapper : MonoBehaviour
{
    void Awake()
    {
        // Disable sleeping on the display machine
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;

        Debug.Log("24/7 Mode Active. Target FPS set to 60.");
    }
}

