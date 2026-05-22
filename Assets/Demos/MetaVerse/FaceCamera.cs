using Unity.Cinemachine;
using UnityEngine;

public class FaceCamera : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        Vector3 dir = Camera.main.transform.position - transform.position;
        dir = dir.ProjectOntoPlane(Vector3.up).normalized;

        transform.LookAt(transform.position - dir, Vector3.up);
    }
}
