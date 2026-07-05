import os
import xml.etree.ElementTree as ET

locales = {
    "es": {
        "connection_error_cert_changed": "La identidad de seguridad de este PC ha cambiado. Si reinstalaste RemEx, elimina el emparejamiento y vuelve a emparejar.",
        "connection_action_repair": "Volver a emparejar",
        "wake_pc_sent": "Señal de encendido enviada",
        "wake_pc_failed": "Error al enviar la señal de encendido",
        "wake_pc_mac_not_configured": "Dirección MAC no configurada — configúrala en Ajustes",
        "wake_pc_lib_not_loaded": "No se puede enviar la señal de encendido",
        "pairing_error_generic": "Error de emparejamiento — por favor, inténtalo de nuevo",
        "splash_command_your": "CONTROLA ",
        "splash_pc": "TU PC",
        "splash_command_center": "⚡ CENTRO DE CONTROL",
        "splash_tap_to_skip": "Toca en cualquier lugar para omitir"
    },
    "fr": {
        "connection_error_cert_changed": "L'identité de sécurité de ce PC a changé. Si vous avez réinstallé RemEx, supprimez le couplage et recommencez.",
        "connection_action_repair": "Recoupler",
        "wake_pc_sent": "Signal de réveil envoyé",
        "wake_pc_failed": "Échec de l'envoi du signal de réveil",
        "wake_pc_mac_not_configured": "Adresse MAC non configurée — à définir dans les paramètres",
        "wake_pc_lib_not_loaded": "Impossible d'envoyer le signal de réveil",
        "pairing_error_generic": "Échec du couplage — veuillez réessayer",
        "splash_command_your": "CONTRÔLEZ ",
        "splash_pc": "VOTRE PC",
        "splash_command_center": "⚡ CENTRE DE CONTRÔLE",
        "splash_tap_to_skip": "Touchez n'importe où pour passer"
    },
    "hi": {
        "connection_error_cert_changed": "इस PC की सुरक्षा पहचान बदल गई है। यदि आपने RemEx को फिर से इंस्टॉल किया है, तो पेयरिंग हटाएं और फिर से पेयर करें।",
        "connection_action_repair": "फिर से पेयर करें",
        "wake_pc_sent": "वेक सिग्नल भेजा गया",
        "wake_pc_failed": "वेक सिग्नल भेजने में विफल",
        "wake_pc_mac_not_configured": "MAC पता कॉन्फ़िगर नहीं है — सेटिंग्स में सेट करें",
        "wake_pc_lib_not_loaded": "वेक सिग्नल नहीं भेजा जा सकता",
        "pairing_error_generic": "पेयरिंग विफल — कृपया पुनः प्रयास करें",
        "splash_command_your": "अपने PC को ",
        "splash_pc": "नियंत्रित करें",
        "splash_command_center": "⚡ नियंत्रण केंद्र",
        "splash_tap_to_skip": "छोड़ने के लिए कहीं भी टैप करें"
    },
    "in": {
        "connection_error_cert_changed": "Identitas keamanan PC ini telah berubah. Jika Anda menginstal ulang RemEx, hapus penyandingan dan sandingkan lagi.",
        "connection_action_repair": "Sandingkan Ulang",
        "wake_pc_sent": "Sinyal bangun dikirim",
        "wake_pc_failed": "Gagal mengirim sinyal bangun",
        "wake_pc_mac_not_configured": "Alamat MAC belum dikonfigurasi — atur di Pengaturan",
        "wake_pc_lib_not_loaded": "Tidak dapat mengirim sinyal bangun",
        "pairing_error_generic": "Penyandingan gagal — silakan coba lagi",
        "splash_command_your": "KENDALIKAN ",
        "splash_pc": "PC ANDA",
        "splash_command_center": "⚡ PUSAT KENDALI",
        "splash_tap_to_skip": "Ketuk di mana saja untuk melewati"
    },
    "pl": {
        "connection_error_cert_changed": "Tożsamość bezpieczeństwa tego komputera uległa zmianie. Jeśli przeinstalowałeś RemEx, usuń parowanie i sparuj ponownie.",
        "connection_action_repair": "Sparuj ponownie",
        "wake_pc_sent": "Sygnał wybudzania wysłany",
        "wake_pc_failed": "Nie udało się wysłać sygnału wybudzania",
        "wake_pc_mac_not_configured": "Adres MAC nie skonfigurowany — ustaw w Ustawieniach",
        "wake_pc_lib_not_loaded": "Nie można wysłać sygnału wybudzania",
        "pairing_error_generic": "Parowanie nie powiodło się — spróbuj ponownie",
        "splash_command_your": "KONTROLUJ ",
        "splash_pc": "SWÓJ KOMPUTER",
        "splash_command_center": "⚡ CENTRUM DOWODZENIA",
        "splash_tap_to_skip": "Dotknij, aby pominąć"
    },
    "pt-rBR": {
        "connection_error_cert_changed": "A identidade de segurança deste PC mudou. Se você reinstalou o RemEx, remova o pareamento e pareie novamente.",
        "connection_action_repair": "Reparear",
        "wake_pc_sent": "Sinal de despertar enviado",
        "wake_pc_failed": "Falha ao enviar sinal de despertar",
        "wake_pc_mac_not_configured": "Endereço MAC não configurado — defina nas Configurações",
        "wake_pc_lib_not_loaded": "Não é possível enviar o sinal de despertar",
        "pairing_error_generic": "Falha no pareamento — tente novamente",
        "splash_command_your": "CONTROLE ",
        "splash_pc": "SEU PC",
        "splash_command_center": "⚡ CENTRO DE COMANDO",
        "splash_tap_to_skip": "Toque em qualquer lugar para pular"
    },
    "tr": {
        "connection_error_cert_changed": "Bu PC'nin güvenlik kimliği değişti. RemEx'i yeniden yüklediyseniz, eşleştirmeyi kaldırın ve tekrar eşleştirin.",
        "connection_action_repair": "Yeniden Eşleştir",
        "wake_pc_sent": "Uyandırma sinyali gönderildi",
        "wake_pc_failed": "Uyandırma sinyali gönderilemedi",
        "wake_pc_mac_not_configured": "MAC adresi yapılandırılmadı — Ayarlar'dan ayarlayın",
        "wake_pc_lib_not_loaded": "Uyandırma sinyali gönderilemiyor",
        "pairing_error_generic": "Eşleştirme başarısız — lütfen tekrar deneyin",
        "splash_command_your": "PC'NİZİ ",
        "splash_pc": "YÖNETİN",
        "splash_command_center": "⚡ KONTROL MERKEZİ",
        "splash_tap_to_skip": "Geçmek için herhangi bir yere dokunun"
    },
    "uk": {
        "connection_error_cert_changed": "Сертифікат безпеки цього ПК змінено. Якщо ви перевстановили RemEx, видаліть пару та виконайте підключення знову.",
        "connection_action_repair": "Підключити знову",
        "wake_pc_sent": "Сигнал пробудження надіслано",
        "wake_pc_failed": "Не вдалося надіслати сигнал пробудження",
        "wake_pc_mac_not_configured": "MAC-адресу не налаштовано — вкажіть у налаштуваннях",
        "wake_pc_lib_not_loaded": "Неможливо надіслати сигнал пробудження",
        "pairing_error_generic": "Помилка підключення — спробуйте ще раз",
        "splash_command_your": "КЕРУЙТЕ ",
        "splash_pc": "СВОЇМ ПК",
        "splash_command_center": "⚡ ЦЕНТР КЕРУВАННЯ",
        "splash_tap_to_skip": "Торкніться, щоб пропустити"
    }
}

base_dir = "/mnt/Shared/RemEx/remex.android/app/src/main/res"

for locale, strings in locales.items():
    file_path = os.path.join(base_dir, f"values-{locale}", "strings.xml")
    if os.path.exists(file_path):
        tree = ET.parse(file_path)
        root = tree.getroot()
        
        # Add strings if they don't exist
        existing_names = set(child.attrib.get('name') for child in root if child.tag == 'string')
        
        for name, value in strings.items():
            if name not in existing_names:
                new_string = ET.Element('string', {'name': name})
                new_string.text = value
                root.append(new_string)
        
        ET.indent(tree, space="    ", level=0)
        tree.write(file_path, encoding="utf-8", xml_declaration=True)
        print(f"Updated {locale}")
    else:
        print(f"Skipped {locale}, file not found")
