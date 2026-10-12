using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Akai.Controller.Logging;
using Akai.Data.Game;

namespace Akai.Persistence.Saving
{
    public class DataPersistenceManager : MonoBehaviour
    {
        [Header("File Storage Config")]

        [SerializeField] public string fileName;

        private GameData gameData;

        private List<IDataPersistence> dataPersistenceObjects;

        private FileDataHandler dataHandler;

        public static DataPersistenceManager instance { get; private set; } // able to get the instance publicly, but only be able to modify the instance privately

        private void Awake()
        {
            if (instance != null)
            {
                Log.Error("Found more than one Data Persistence Manager in the scene.");
            }

            instance = this;
        }

        private void Start()
        {
            this.dataHandler = new FileDataHandler(Application.persistentDataPath, fileName); // Will give the operating system standard directory for persisting data in a unity project
            this.dataPersistenceObjects = FindAllDataPersistenceObjects();
             // Game is loaded on startup
        }

        public void NewGame()
        {
            this.gameData = new GameData(); //create a new game
        }

        public void LoadGame()
        {
            // load any saved data from a file using the data handler
            this.gameData = dataHandler.Load();
            // if no data can be loaded, initialize to a new game
            if (this.gameData == null)
            {
                Log.Message("No data was found. Initializing data to defaults.");
                NewGame();
            }
            // push the loaded data to all other scripts that need it
            foreach (IDataPersistence dataPersistenceObj in dataPersistenceObjects)
            {
                dataPersistenceObj.LoadData(gameData);
            }

            Log.Message("Loaded score count = " + gameData.score);
        }

        public void SaveGame()
        {
            // pass the data to other scripts so they can update it
            foreach (IDataPersistence dataPersistenceObj in dataPersistenceObjects)
            {
                dataPersistenceObj.SaveData(ref gameData);
            }

            Log.Message("Saved score count = " + gameData.score);

            // save the data to file using the data handler
            dataHandler.Save(gameData);
        }

        private List<IDataPersistence> FindAllDataPersistenceObjects()
        {
            IEnumerable<IDataPersistence> dataPersistenceObjects = FindObjectsOfType<MonoBehaviour>().OfType<IDataPersistence>(); //Finds all scripts that implemented the data persistence interface in our scene. Those scripts need to extend from MonoBehaviour to be found in this way

            return new List<IDataPersistence>(dataPersistenceObjects);

        }

    }
}

