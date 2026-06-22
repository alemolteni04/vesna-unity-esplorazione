#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public static class DoorNameSanitizer
{
    [MenuItem("VESNA/Sanitizza nomi porte")]
    static void Sanitize()
    {
        int n = 0;
        foreach (var d in Object.FindObjectsOfType<DoorVarcoScript>(true))
        {
            string clean = d.gameObject.name.Replace(' ', '_').Replace('-', '_');
            if (clean != d.gameObject.name)
            {
                Undo.RecordObject(d.gameObject, "Sanitize door name");
                d.gameObject.name = clean;
                n++;
            }
        }
        Debug.Log($"[Sanitizer] Rinominate {n} porte/varchi.");
    }
}
#endif