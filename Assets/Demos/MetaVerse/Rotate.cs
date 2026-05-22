using UnityEngine;

public class Rotate : MonoBehaviour
{
    public float RotateSpeed = 80;
    public Vector3 RotateAxis = Vector3.up; 
    public Space RotateSpace = Space.Self;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        transform.Rotate(RotateAxis, RotateSpeed * Time.deltaTime, RotateSpace);
    }
}
