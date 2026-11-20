# Разработка SonaDesk

## Сборка

Понадобятся Windows x64, .NET SDK 8 и Node.js 24. Выполните команду в PowerShell из корня репозитория.

```powershell
.\Source\Build-Desktop.ps1
```

Скрипт создаёт папку `SonaDesk` с `SonaDesk.exe` и подпапкой `app`. Для выпуска архива используйте `.\Source\Build-Portable.ps1`. Оба скрипта принимают `-OutputDirectory`, `-Zig` и `-SdkHeaders`.

Workflow `Build Windows` запускается вручную во вкладке Actions. Он собирает текущие исходники на Windows и сохраняет `SonaDesk.zip` в артефакте `sonadesk-windows-unsigned` на три дня. Этот артефакт предназначен для дальнейшей подписи и проверки запуска.

Для подписанной сборки передайте `-SigningScript` со скриптом вашего сервиса подписи. Он получает массив абсолютных путей `-Paths` и подписывает файлы на месте. Сначала вызывается подпись нативных DLL, затем собирается аудиохост с подписанным APO, после чего подписываются остальные EXE и DLL. Перед упаковкой проверяется доверие Windows к каждой подписи. Ключи и параметры доступа к сервису должны храниться вне репозитория.

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

## Code signing policy

SonaDesk is preparing to apply to [SignPath Foundation](https://signpath.org/apply) for free signing. The project has not been accepted, no signing account is connected, and release 0.7.2 remains unsigned. The [MIT license](../LICENSE) covers SonaDesk's own code. Dependencies retain their original licenses.

The intended signing provider, subject to acceptance, is SignPath.io, with a certificate held by SignPath Foundation. This would make SignPath Foundation the publisher displayed by Windows; copyright in SonaDesk remains with ekshenXV.

- Author, reviewer, and release approver: [ekshenXV](https://github.com/ekshenXV).
- Signing requests must originate from a verifiable GitHub-hosted build of this repository and require the maintainer's manual approval.
- Multi-factor authentication is required for the maintainer's GitHub and SignPath accounts. Its configuration still needs to be confirmed during enrollment.
- Signing coverage must be agreed with SignPath before integration. Upstream Electron and voice-isolation binaries cannot simply be signed as SonaDesk's own code under the Foundation's [terms](https://signpath.org/terms). A signature on the launcher alone does not establish compatibility with Smart App Control for all loaded components.
- Enrollment also requires reviewing system-change notices and complete removal instructions for the installed APO. The application currently installs a system module in addition to its portable folder.

Privacy: audio, voice-isolation recordings, plugin settings, and presets are processed locally. The app checks GitHub for release information at startup and downloads an update when the user requests it. These requests expose normal connection information, including an IP address, to GitHub; they do not upload audio or presets. User-added VST plugins run third-party code and may have their own network behavior. SonaDesk does not include analytics or an audio-upload service.
