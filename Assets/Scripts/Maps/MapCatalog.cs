using UnityEngine;

/// <summary>
/// Every map the game can load as a prefab with a <see cref="MapDefinition"/> on its root (Maps/Resources/MapCatalog).
/// The warehouse is not listed: it is baked into Start.unity (<see cref="MapLoader.SceneMapId"/>). Adding a map = one
/// entry here plus its id in the lobby backend's MAPS list.
/// </summary>
[CreateAssetMenu(fileName = "MapCatalog", menuName = "FPS/Map Catalog")]
public class MapCatalog : ScriptableObject {

    public MapDefinition[] maps;

    private static MapCatalog instance;

    public static MapCatalog Instance {
        get {
            if (instance == null) instance = Resources.Load<MapCatalog>("MapCatalog");
            return instance;
        }
    }

    public MapDefinition Find(string id) {
        if (maps == null) return null;
        foreach (MapDefinition m in maps) {
            if (m != null && m.id == id) return m;
        }
        return null;
    }

}
