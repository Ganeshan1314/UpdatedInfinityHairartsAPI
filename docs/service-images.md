# Service image deployment

The admin app now displays the user-provided `hairCut.jpg` for every service
card. Its copy at `D:\GitHub\SalonexaAdmin\src\assets\services\hairCut.jpg`
is bundled into the web build and APK. The server image deployment below applies
to individual service photo URLs, including other API consumers and editor previews.

The configured server returned HTTP 404 for the existing haircut image URLs.
The earlier API publish omitted `Images/HairCutItemListImages` because these
files are outside `wwwroot`. The project now explicitly includes JPG, JPEG, PNG
and WebP files from that directory when publishing. The API already serves this
directory at `/Images/HairCutItemListImages`.

Place original service photos in this local folder:

```text
D:\GitHub\UpdatedInfinityHairartsAPI\InfinityHairartsAPI\Images\HairCutItemListImages
```

The following photos were not found in the local projects. Save/export their
original photos as JPG with these exact names, matching the database:

| Service | Filename |
| --- | --- |
| Face cleaning | `2e84001c-3dd3-4734-94e5-11085e968f7f.jpg` |
| HairCut, Drim, Face Pack | `1a7548b5-7b4e-4d02-af7b-3b006602cbba.jpg` |

Four missing local database photos were recovered under their original filenames
from the existing `InfinityHairArtsAdmin` project. No existing photos were replaced.
The 32 available photos were published and verified through the actual API on
loopback: every file returned HTTP 200, an image content type, and unchanged bytes.
These files cover 18 of the 20 image names returned by the hosted menu endpoint.

To repair the current hosted installation, merge the `Images` folder from the
images-only package into the API's physical application directory, alongside
`InfinityHairartsAPI.dll` and `web.config`. Keep any existing server images.
Copy the two originals above into that same server image directory when available.
Adding files to the local project does not upload them to the server.

For future releases, publish the API normally; the image directory is now included
automatically. No SQL changes are needed to restore these existing filenames.
Verify deployment by opening a known photo URL, for example:

```text
https://103.75.157.142/Images/HairCutItemListImages/557b2117-bd6f-4a3b-a3e1-f85ee0c5f61c.jpg
```

It must return the photo with HTTP 200. The admin's Refresh action retries images
that failed earlier. Photos that are still absent retain the existing placeholder.
