# RemEx 2.5.0 — Android "What's new"

Play Store release notes, one block per shipped locale. Android-scoped only: nothing here is a
PC-side change. Budget is 100 characters per block, so this is three headliners, not a summary.
The full record is [`docs/RELEASE-NOTES-2.5.0.md`](docs/RELEASE-NOTES-2.5.0.md).

Paste the blocks below verbatim into the Play Console listing.

<en-US>-Clipboard and screenshots to PC
-Send whole folders
-Security and pairing fixes</en-US>

<es-ES>-Portapapeles y capturas al PC
-Envía carpetas enteras
-Correcciones de seguridad</es-ES>

<fr-FR>-Presse-papiers et captures vers le PC
-Envoi de dossiers entiers
-Correctifs de sécurité</fr-FR>

<hi-IN>-क्लिपबोर्ड और स्क्रीनशॉट PC पर भेजें
-पूरे फ़ोल्डर भेजें
-सुरक्षा और पेयरिंग सुधार</hi-IN>

<id>-Kirim teks salinan & tangkapan ke PC
-Kirim seluruh folder
-Perbaikan keamanan</id>

<pl-PL>-Schowek i zrzuty ekranu do PC
-Wysyłanie całych folderów
-Poprawki zabezpieczeń</pl-PL>

<pt-BR>-Envie textos copiados e capturas ao PC
-Envie pastas inteiras
-Correções de segurança</pt-BR>

<tr-TR>-Pano ve ekran görüntüsü gönderme
-Tüm klasörleri gönderme
-Güvenlik düzeltmeleri</tr-TR>

<uk>-Буфер обміну та знімки на ПК
-Надсилання цілих папок
-Виправлення безпеки та сполучення</uk>

## What the three lines cover

| Line | Android work behind it |
|---|---|
| Clipboard and screenshots to PC | Phone-to-PC clipboard send (RemEx-hgqs, RemEx-qu3t); a Screenshot button on the phone, saved as PNG under Pictures/RemEx Screenshots and offered back to the phone (RemEx-66rf, RemEx-tjve, RemEx-86nu, RemEx-y7my, RemEx-byij) |
| Send whole folders | Whole-folder transfer, including empty folders, which previously meant opening each subfolder and selecting its contents (RemEx-q3twg) |
| Security and pairing fixes | TLS pinning fails closed with no pin (RemEx-s032.5); Remote Desktop cert pinning actually enforced (RemEx-xmgw, RemEx-mlce); a push can no longer skip the phone's consent prompt (RemEx-z6lh); forgetting a PC now clears the reconnect secret as well as the pin, so re-pairing stopped failing on the next connect (RemEx-1phe, RemEx-vnps, RemEx-j9ei) |

## Left out, and why

The 100-character budget fits three lines. These were the next candidates:

- **Transfer speed and time remaining** (RemEx-8c3v, RemEx-qmiv). Real, but it reads as a refinement
  of a feature users already have, where folder transfer is a capability they did not.
- **Save and switch between PCs** (RemEx-k62t, RemEx-8ih5). Matters most to people with two machines.
- **Dynamic colour toggle, Neutral and Monochrome styles** (RemEx-2xsy, RemEx-9429, RemEx-6byw) and
  **"Remove animations" support** (RemEx-tej8, RemEx-n39x). Both worth saying, both lose to the three
  above on a phone.
- **Media and volume control, correct play/pause icon** (RemEx-3cnq, RemEx-hulc, RemEx-xx6xf).
- **Wake-on-LAN without typing a MAC address** (RemEx-izuj).
