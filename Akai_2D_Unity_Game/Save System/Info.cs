using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Akai.Controller.Logging;

namespace Akai.Persistence.Saving
{
    public class Info : MonoBehaviour
    {

        void Start()
        {
            Log.Message(Application.persistentDataPath);
        }
    }

}
