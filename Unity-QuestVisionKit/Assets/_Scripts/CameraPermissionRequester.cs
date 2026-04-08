using UnityEngine;
using UnityEngine.Android;

public class CameraPermissionRequester : MonoBehaviour
{
    private const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    private void Start()
    {
        if (!Permission.HasUserAuthorizedPermission(CameraPermission))
        {
            Permission.RequestUserPermission(CameraPermission);
        }
    }
}
