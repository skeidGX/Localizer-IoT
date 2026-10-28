# Localizer-IoT — русификация Windows IoT Enterprise LTSC

Windows Forms-приложение для обслуживания установочных ISO офлайн с помощью DISM и Windows ADK.

## Сборка

Автономный single-file EXE для Windows x64 собирается на GitHub Actions. [Сборки и артефакты](https://github.com/skeidGX/Localizer-IoT/actions).

## Что реализовано

- GUI на русском языке, выбор ISO и поддержка drag-and-drop.
- Анализ `install.wim` / `install.esd`, индексов, имени редакции, версии и архитектуры через DISM.
- Отдельные источники для Language Pack ISO, Features on Demand ISO/папки и русских Setup-ресурсов `sources\\ru-ru`.
- Автопоиск CAB-пакетов в заданных источниках плюс добавление CAB вручную.
- По умолчанию обрабатываются все индексы `install.wim`; можно выбрать один. `install.esd` экспортируется во временный WIM, исходный ISO не изменяется.
- Проверка версии и архитектуры основного языкового CAB до внедрения. CAB, похожие на LCU/SSU/KB или MSU, не применяются — обновлять Windows программа не должна.
- Внедрение русского Language Pack; попытка установки Language Features on Demand (Basic, Fonts, OCR, Speech, Text-to-Speech, Handwriting), когда есть совместимые источники.
- Настройки ru-RU для языка интерфейса, системной и пользовательской локали, русской клавиатуры по умолчанию и английской дополнительной, часового пояса и `UILanguageFallback=en-US`.
- Обработка `boot.wim` и встроенного `winre.wim` только при наличии пакетов, совместимых с их базовой сборкой и архитектурой; несовместимый WinPE-пакет не должен внедряться молча.
- Создание `autounattend.xml`, если его ещё нет. Существующий файл сохраняется, потому что он может содержать настройки владельца.
- Генерация `lang.ini`, отдельный загрузочный ISO, журнал и отчёт `.report.txt`. Предыдущий выходной файл остаётся нетронутым до успешного создания нового ISO.
- Если `oscdimg.exe` отсутствует, программа может предложить скачать официальный установщик Microsoft ADK, проверить его подпись и установить только Deployment Tools.

## Что пока требует пользователя и тестирования

- **Автоматическое скачивание подходящего Language Pack/FOD ISO не реализовано.** Для IoT Enterprise LTSC языковые пакеты, FOD и WinPE optional components должны соответствовать нужной версии/сборке/архитектуре. Доступ к LP/FOD медиа может зависеть от лицензии и канала поставки. Универсального публичного прямого адреса для подходящего LP/FOD ISO нет; UUP Dump API возвращает наборы UUP-файлов для создания Windows media, а не универсальный LP/FOD ISO. Получите медиа из официального источника, доступного для вашей лицензии.
- Основной CAB Language Pack `ru-RU`, FOD-источник и Setup-ресурсы нужно указать в интерфейсе. Обычно это отдельные медиа.
- Для полной русификации Setup нужны `lp.cab`, WinPE-Setup CAB-компоненты именно подходящей версии и файлы `sources\\ru-ru`. Если они отсутствуют или несовместимы, приложение добавит предупреждения в отчёт; отдельные экраны могут остаться на английском.
- Для локализации WinPE add-on сам по себе не скачивается и не устанавливается автоматически. Текущая версия только проверяет наличие совместимых компонентов в установленном ADK/указанном источнике и пропускает небезопасные по версии компоненты.
- Сборка EXE проверена GitHub Actions, но end-to-end проверка не завершена: нужен реальный Windows IoT Enterprise LTSC ISO и тестирование полученного ISO в виртуальной машине. Успешная компиляция не доказывает, что итоговый носитель загрузится на конкретном устройстве.

## Требования

- Windows 10/11 x64 как рабочая станция обслуживания.
- Права администратора (запуск приложения запрашивает их).
- Language Pack CAB `ru-RU` для целевой редакции, версии и архитектуры.
- Совместимый Languages and Optional Features/FOD источник.
- Для полной локализации Windows Setup — совпадающие WinPE language optional components и файлы `sources\\ru-ru`.
- Интернет нужен только для автоматической загрузки bootstrapper ADK; языковые источники должны быть предоставлены пользователем.

## Безопасность

Исходный ISO не изменяется: работа идёт в отдельной временной папке, а конечный ISO заменяет выбранный выходной файл только после успешного построения. Сначала тестируйте результат в виртуальной машине, затем на целевом устройстве.

Полезные официальные материалы:

- [Microsoft: Add languages to Windows images](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-language-packs-to-windows?view=windows-11)
- [Microsoft: Add languages to Windows Setup](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/add-multilingual-support-to-windows-setup?view=windows-11)
- [Microsoft: Download and install the Windows ADK](https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install)
- [UUP Dump JSON API](https://git.uupdump.net/uup-dump/json-api)
