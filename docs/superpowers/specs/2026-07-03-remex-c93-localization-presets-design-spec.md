# Spec: Remote Desktop Quality Presets Localization (RemEx-c93)

## Context & Objectives
Issue **RemEx-c93** covers the localization of remote desktop quality preset labels and warning messages added in **RemEx-vj31**. These strings currently only exist in the default `values/strings.xml` in English. 

To ensure ship-quality localization, we will translate the preset labels, uncapped-overflow warnings, and resolution scale strings into the 8 supported locales:
- Spanish (`es`)
- French (`fr`)
- Hindi (`hi`)
- Indonesian (`in`)
- Polish (`pl`)
- Portuguese - Brazil (`pt-rBR`)
- Turkish (`tr`)
- Ukrainian (`uk`)

## Architectural Strategy
The translations will be added as parallel contiguous XML blocks inside each localized `strings.xml` file.
To maintain strict file parallelism across all resource files, the keys will be inserted between:
- `remote_desktop_fps_overlay_btn`
- `remote_desktop_left_initial`

Which corresponds to line 488 and line 489 in each of the 8 localized files.

## Keys & Translations

### 1. Spanish (`es`)
- `remote_desktop_presets_label`: Ajustes preestablecidos
- `remote_desktop_preset_unlimited`: Ilimitado
- `remote_desktop_preset_smooth_sharp`: Fluido y nítido
- `remote_desktop_preset_balanced`: Equilibrado
- `remote_desktop_preset_data_saver`: Ahorro de datos
- `remote_desktop_preset_custom`: Personalizado
- `remote_desktop_preset_unlimited_info`: Sin límite de calidad ni de tasa de fotogramas: utiliza todo lo que tu dispositivo y conexión puedan soportar. El rendimiento puede variar.
- `remote_desktop_preset_unlimited_overflow_warning`: El modo Ilimitado puede consumir mucho ancho de banda y batería, y puede causar interrupciones si tu red o tu PC no pueden seguir el ritmo. Prueba Fluido y nítido si eso sucede.
- `remote_desktop_scale_label`: Escala de resolución: %1$d%%

### 2. French (`fr`)
- `remote_desktop_presets_label`: Préréglages
- `remote_desktop_preset_unlimited`: Illimité
- `remote_desktop_preset_smooth_sharp`: Fluide et net
- `remote_desktop_preset_balanced`: Équilibré
- `remote_desktop_preset_data_saver`: Économiseur de données
- `remote_desktop_preset_custom`: Personnalisé
- `remote_desktop_preset_unlimited_info`: Aucune limite de qualité ou de fréquence d\'images — utilise tout ce que votre appareil et votre connexion peuvent supporter. Les performances peuvent varier.
- `remote_desktop_preset_unlimited_overflow_warning`: Le mode Illimité peut consommer beaucoup de bande passante et de batterie, et peut provoquer des saccades si votre réseau ou votre PC ne suit pas. Essayez Fluide et net si cela se produit.
- `remote_desktop_scale_label`: Échelle de résolution : %1$d%%

### 3. Hindi (`hi`)
- `remote_desktop_presets_label`: प्रीसेट्स
- `remote_desktop_preset_unlimited`: असीमित
- `remote_desktop_preset_smooth_sharp`: स्मूथ और शार्प
- `remote_desktop_preset_balanced`: संतुलित
- `remote_desktop_preset_data_saver`: डेटा सेवर
- `remote_desktop_preset_custom`: कस्टम
- `remote_desktop_preset_unlimited_info`: क्वालिटी या फ्रेमरेट की कोई सीमा नहीं — यह आपके डिवाइस और कनेक्शन की पूरी क्षमता का उपयोग करता है। प्रदर्शन अलग-अलग हो सकता है।
- `remote_desktop_preset_unlimited_overflow_warning`: असीमित मोड बहुत अधिक बैंडविड्थ और बैटरी की खपत कर सकता है, और यदि आपका नेटवर्क या PC इसका सामना नहीं कर पाता है तो इसमें रुकावट आ सकती है। यदि ऐसा होता है, तो स्मूथ और शार्प मोड आज़माएं।
- `remote_desktop_scale_label`: रिज़ॉल्यूशन स्केल: %1$d%%

### 4. Indonesian (`in`)
- `remote_desktop_presets_label`: Prasetel
- `remote_desktop_preset_unlimited`: Tanpa batas
- `remote_desktop_preset_smooth_sharp`: Mulus &amp; Tajam
- `remote_desktop_preset_balanced`: Seimbang
- `remote_desktop_preset_data_saver`: Penghemat Data
- `remote_desktop_preset_custom`: Kustom
- `remote_desktop_preset_unlimited_info`: Tidak ada batas kualitas atau kecepatan bingkai — menggunakan semua yang dapat ditangani oleh perangkat dan koneksi Anda. Performa mungkin bervariasi.
- `remote_desktop_preset_unlimited_overflow_warning`: Mode Tanpa batas dapat menggunakan banyak bandwidth dan baterai, serta dapat menyebabkan tersendat jika jaringan atau PC Anda tidak mampu mengimbanginya. Coba gunakan Mulus &amp; Tajam jika itu terjadi.
- `remote_desktop_scale_label`: Skala resolusi: %1$d%%

### 5. Polish (`pl`)
- `remote_desktop_presets_label`: Presety
- `remote_desktop_preset_unlimited`: Bez limitu
- `remote_desktop_preset_smooth_sharp`: Płynny i ostry
- `remote_desktop_preset_balanced`: Zrównoważony
- `remote_desktop_preset_data_saver`: Oszczędzanie danych
- `remote_desktop_preset_custom`: Niestandardowy
- `remote_desktop_preset_unlimited_info`: Brak limitu jakości i liczby klatek na sekundę — wykorzystuje pełnię możliwości Twojego urządzenia i połączenia. Wydajność może się różnić.
- `remote_desktop_preset_unlimited_overflow_warning`: Tryb Bez limitu może zużywać dużo przepustowości i baterii oraz może powodować zacinanie się, jeśli Twoja sieć lub komputer nie nadążają. Jeśli tak się stanie, wypróbuj ustawienie Płynny i ostry.
- `remote_desktop_scale_label`: Skala rozdzielczości: %1$d%%

### 6. Portuguese - Brazil (`pt-rBR`)
- `remote_desktop_presets_label`: Predefinições
- `remote_desktop_preset_unlimited`: Ilimitado
- `remote_desktop_preset_smooth_sharp`: Fluido e nítido
- `remote_desktop_preset_balanced`: Equilibrado
- `remote_desktop_preset_data_saver`: Economia de dados
- `remote_desktop_preset_custom`: Personalizado
- `remote_desktop_preset_unlimited_info`: Sem limite de qualidade ou taxa de quadros — utiliza tudo o que o seu dispositivo e conexão podem suportar. O desempenho pode variar.
- `remote_desktop_preset_unlimited_overflow_warning`: O modo Ilimitado pode consumir muita largura de banda e bateria, e pode causar travamentos se a sua rede ou o seu PC não acompanharem. Tente usar Fluido e nítido se isso acontecer.
- `remote_desktop_scale_label`: Escala de resolução: %1$d%%

### 7. Turkish (`tr`)
- `remote_desktop_presets_label`: Ön ayarlar
- `remote_desktop_preset_unlimited`: Sınırsız
- `remote_desktop_preset_smooth_sharp`: Akıcı ve Keskin
- `remote_desktop_preset_balanced`: Dengeli
- `remote_desktop_preset_data_saver`: Veri Tasarrufu
- `remote_desktop_preset_custom`: Özel
- `remote_desktop_preset_unlimited_info`: Kalite veya kare hızı sınırı yok — cihazınızın ve bağlantınızın kaldırabileceği her şeyi kullanır. Performans değişiklik gösterebilir.
- `remote_desktop_preset_unlimited_overflow_warning`: Sınırsız modu çok fazla bant genişliği ve pil tüketebilir; ağınız veya bilgisayarınız yetişemezse takılmalar olabilir. Böyle bir durumda Akıcı ve Keskin modunu deneyin.
- `remote_desktop_scale_label`: Çözünürlük ölçeği: %1$d%%

### 8. Ukrainian (`uk`)
- `remote_desktop_presets_label`: Пресети
- `remote_desktop_preset_unlimited`: Безлімітний
- `remote_desktop_preset_smooth_sharp`: Плавний і чіткий
- `remote_desktop_preset_balanced`: Збалансований
- `remote_desktop_preset_data_saver`: Економія трафіку
- `remote_desktop_preset_custom`: Спеціальний
- `remote_desktop_preset_unlimited_info`: Без обмежень якості чи частоти кадрів — використовує все, що можуть забезпечити ваш пристрій і з’єднання. Продуктивність може відрізнятися.
- `remote_desktop_preset_unlimited_overflow_warning`: Режим Безлімітний може споживати багато трафіку та заряду акумулятора, а також викликати затримки, якщо ваша мережа або ПК не справляються. Якщо це стається, спробуйте режим Плавний і чіткий.
- `remote_desktop_scale_label`: Масштаб роздільної здатності: %1$d%%

## Verification Plan
1. **Compilation**: Run Gradle clean and assemble tasks on `remex.android` (`./gradlew remexFreshAssembleDebug`).
2. **Review**: Ensure no XML encoding/parsing errors.
