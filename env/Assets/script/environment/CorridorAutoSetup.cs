#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// ============================================================
// CorridorAutoSetup.cs  — Editor Only
// ============================================================
// Quando l'utente trascina un prefab CorridorData nella scena,
// assegna automaticamente corridorId incrementale:
//   corridor_1, corridor_2, corridor_3, ...
//
// L'utente può poi rinominarlo nell'Inspector e il nome
// si propaga ai figli automaticamente (CorridorManagerEditor).
// ============================================================

[InitializeOnLoad]
public static class CorridorAutoSetup
{
    static CorridorAutoSetup()
    {
        ObjectFactory.componentWasAdded += OnComponentAdded;
    }

    private static void OnComponentAdded(Component component)
    {
        if (component is not CorridorData corridor) return;

        string newId = GetNextId();
        corridor.corridorId      = newId;
        corridor.gameObject.name = newId;

        var poles = corridor.GetComponentsInChildren<CorridorPoleScript>();
        if (poles.Length > 0) poles[0].gameObject.name = $"Pole_1_{newId}";
        if (poles.Length > 1) poles[1].gameObject.name = $"Pole_2_{newId}";

        EditorUtility.SetDirty(corridor);
        Debug.Log($"[CorridorAutoSetup] Creato: {newId}");
    }

    private static string GetNextId()
    {
        var all = Object.FindObjectsByType<CorridorData>(FindObjectsSortMode.None);
        int max = 0;
        foreach (var c in all)
        {
            var parts = c.corridorId.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out int n))
                if (n > max) max = n;
        }
        return $"corridor_{max + 1}";
    }
}

// ============================================================
// Custom Inspector per CorridorData
// Mostra corridorId modificabile con rename live
// ============================================================
[CustomEditor(typeof(CorridorData))]
public class CorridorDataEditor : Editor
{
    private string previousId;

    void OnEnable()
    {
        previousId = ((CorridorData)target).corridorId;
    }

    public override void OnInspectorGUI()
    {
        var corridor = (CorridorData)target;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Corridoio", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        string newId = EditorGUILayout.TextField("Corridor ID", corridor.corridorId);
        if (EditorGUI.EndChangeCheck() && newId != previousId)
        {
            if (IsIdTaken(newId, corridor))
            {
                EditorUtility.DisplayDialog("ID già usato",
                    $"'{newId}' è già usato da un altro corridoio.", "OK");
            }
            else
            {
                Undo.RecordObject(corridor, "Rename Corridor");
                corridor.corridorId = newId;
                previousId          = newId;
                corridor.OnCorridorIdChanged();
                EditorUtility.SetDirty(corridor);
            }
        }

        EditorGUILayout.Space(4);
        var poles = corridor.GetComponentsInChildren<CorridorPoleScript>();
        EditorGUILayout.LabelField($"Poli trovati: {poles.Length} / 2",
            poles.Length == 2 ? EditorStyles.label : EditorStyles.boldLabel);

        if (poles.Length != 2)
            EditorGUILayout.HelpBox("Servono esattamente 2 CorridorPoleScript figli.",
                MessageType.Warning);

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Refresh figli"))
            corridor.OnCorridorIdChanged();
    }

    private bool IsIdTaken(string id, CorridorData self)
    {
        foreach (var c in FindObjectsByType<CorridorData>(FindObjectsSortMode.None))
            if (c != self && c.corridorId == id) return true;
        return false;
    }
}
#endif
