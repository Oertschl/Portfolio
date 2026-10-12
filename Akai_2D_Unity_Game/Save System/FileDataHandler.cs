using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;
using Akai.Controller.Logging;
using Akai.Data.Game;

namespace Akai.Persistence.Saving
{
    public class FileDataHandler
    {
        private string dataDirPath = "";

        private string dataFileName = "";

        public FileDataHandler(string dataDirPath, string dataFileName)
        {
            this.dataDirPath = dataDirPath;
            this.dataFileName = dataFileName;
        }

        public GameData Load()
        {
            // use Path.Combine to account for diffrent OS's having different path seperators
            string fullPath = Path.Combine(dataDirPath, dataFileName);
            GameData loadedData = null;
            if (File.Exists(fullPath))
            {
                try
                {
                    // load the serialized data from the file
                    string dataToLoad = "";
                    // we use using() blocks as they ensure that the connection to that file is closed once we're done reading or writing to it
                    using (FileStream stream = new FileStream(fullPath, FileMode.Open))
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            dataToLoad = reader.ReadToEnd();
                        }
                    }
                    // deserialize the data from Json back into the C# object
                    loadedData = JsonUtility.FromJson<GameData>(dataToLoad);
                }
                catch (Exception e)
                {
                    Log.Error("Error occured when trying to load data from file: " + fullPath + "\n" + e);
                }
            }
            return loadedData;
        }

        public void Save(GameData data)
        {
            // use Path.Combine to account for diffrent OS's having different path seperators
            string fullPath = Path.Combine(dataDirPath, dataFileName);
            try
            {
                // create the directory the file will be written to if it doesn't already exist
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

                // serialize the C# game data object into Json
                string dataToStore = JsonUtility.ToJson(data, true); // the second parameter true is optional, it let's us format the Json

                // write the serialized data to the file
                // we use using() blocks as they ensure that the connection to that file is closed once we're done reading or writing to it
                using (FileStream stream = new FileStream(fullPath, FileMode.Create))
                {
                    using (StreamWriter writer = new StreamWriter(stream))
                    {
                        writer.Write(dataToStore);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("Error occured when trying to save data to file: " + fullPath + "\n" + e);
            }
        }
    }
}

