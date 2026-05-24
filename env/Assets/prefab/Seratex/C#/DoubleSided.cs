using UnityEngine;

[ExecuteInEditMode]
public class FixNormals : MonoBehaviour
{
    void OnEnable()
    {
        foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            Vector3[] normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++)
                normals[i] = -normals[i];
            mesh.normals = normals;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                int[] tris = mesh.GetTriangles(i);
                for (int j = 0; j < tris.Length; j += 3)
                {
                    int tmp = tris[j];
                    tris[j] = tris[j + 1];
                    tris[j + 1] = tmp;
                }
                mesh.SetTriangles(tris, i);
            }
        }
    }
}