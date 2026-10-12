using Akai.Data.Game;


namespace Akai.Persistence.Saving
{
    public interface IDataPersistence
    {
        void LoadData(GameData data);     // the implementing script only cares about reading the data

        void SaveData(ref GameData data); // we pass the reference here so that when we save data, we allow the implementing script to modify the data
    }
}

