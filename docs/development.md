# Разработка SonaDesk

## Сборка

Понадобятся Windows x64, .NET SDK 8 и Node.js 24. Выполните команду в PowerShell из корня репозитория.

```powershell
.\Source\Build-Desktop.ps1
```

Скрипт создаёт папку `SonaDesk` с `SonaDesk.exe` и подпапкой `app`. Для выпуска архива используйте `.\Source\Build-Portable.ps1`. Оба скрипта принимают `-OutputDirectory`, `-Zig` и `-SdkHeaders`.

## Релизы

Версия указана в `Source/Desktop/package.json`, `Source/Desktop/package-lock.json` и `Source/MicEffects.csproj`. Прикрепите `SonaDesk.zip` к стабильному GitHub Release с тегом `vX.Y.Z`, совпадающим с версией приложения.

Обновлятор проверяет релизы репозитория из `Source/Desktop/update-config.json`. Пустое поле `repository` отключает проверку. SHA-256 берётся из метаданных архива GitHub.

## Аудио и совместимость

- Выпуск 0.7.2 содержит неподписанные EXE и DLL. Windows 11 с включённым Smart App Control может блокировать его до открытия окна. Проверка запуска в Windows Sandbox не подтверждает совместимость с этой защитой.
- Для выпуска с доверенной подписью нужно подписать исполняемые компоненты и проверить запуск с включённым Smart App Control. APO нужно подписывать до встраивания в `SonaDesk.Audio.exe`, затем подписывать сам аудиохост. Подписи компонентов Electron, VST3-хоста и изоляции голоса также требуют проверки. [Требования Microsoft](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)
- Эквалайзер обрабатывает моно- и стереозвук в общем режиме Windows на выходах, где загружается APO. VST2 и VST3 применяются к микрофону.
- APO передаёт звук в приложение через общую память. Пресет хранится в `%LOCALAPPDATA%\MicEffects\preset.json`. Запись для сравнения изоляции обрабатывается в памяти.
- Если Windows не загружает APO, приложение восстанавливает предыдущие настройки захвата и показывает ошибку. Модуль поставляется без цифровой подписи. Первое подключение APO к физическому устройству на чистой установке Windows ещё предстоит проверить.
- Обмен звуком между процессами добавляет около 35 мс буферизации плюс задержку эффекта. Изоляция голоса сводит стереовход в моно и добавляет 60 мс при 48 кГц.

Лицензии Electron, React, [NAudio](third-party/NAudio-LICENSE.txt) и интерфейсов VST3 приложены к программе. Изоляция голоса использует [DPDFNet](https://github.com/ceva-ip/DPDFNet) и [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) с [ONNX Runtime](https://github.com/microsoft/onnxruntime). Их файлы и лицензии находятся в `app/resources/voice-isolation`.
