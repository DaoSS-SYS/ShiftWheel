# ShiftWheel

Конвертер файлов для Windows в духе Tangerine для Mac. Берёте файл в Проводнике или на рабочем столе, начинаете его перетаскивать и нажимаете **Shift**: вокруг курсора появляется колесо. Бросаете файл на сектор, и рядом с оригиналом появляется результат.

**Стек:** C#, .NET 10, WPF и Windows Forms, ffmpeg, Microsoft Office (COM).

## Установка и запуск

Требуется Windows 10/11 и [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Соберите приложение (команда в разделе «Сборка»). Готовый файл появится в `ShiftWheel\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\ShiftWheel.exe`.
2. Запустите `ShiftWheel.exe`. В трее появится оранжевая иконка.
3. Для картинок, видео и аудио нужен **ffmpeg**: `winget install Gyan.FFmpeg`.
4. Правый клик по иконке в трее → **«Запускать вместе с Windows»**, чтобы приложение стартовало само.

## Что умеет

| Тип файла | Внешнее кольцо (форматы) | Внутреннее кольцо (инструменты) |
|---|---|---|
| Картинки (png, jpg, webp, bmp, gif, tiff, ico, avif) | PNG, JPG, WEBP, PDF, TXT (OCR), DOCX (OCR), GIF, ICO, BMP, TIFF | Сжать, без метаданных, размер 50%, повернуть |
| Видео (mp4, mkv, mov, avi, webm…) | MP4, MKV, WEBM, MOV, AVI, GIF, MP3, WAV, M4A | Сжать, без метаданных, размер 50%, повернуть |
| Аудио (mp3, wav, flac, ogg, m4a…) | MP3, WAV, FLAC, OGG, M4A, AAC, OPUS | Сжать, без метаданных |
| PDF | PNG, JPG, WEBP, BMP, TXT (OCR), DOCX (OCR) | нет |
| Word (doc, docx, rtf, odt) | PDF, DOCX, TXT | нет |
| PowerPoint (ppt, pptx, odp) | PDF, PNG и JPG (по файлу на каждый слайд) | нет |
| Excel (xls, xlsx, ods) | PDF, XLSX, CSV (первый лист) | нет |

* Можно перетаскивать сразу несколько файлов одного типа.
* Результат сохраняется рядом с исходником. Оригинал не изменяется, при совпадении имени добавляется `(1)`.
* Внизу справа появляется карточка прогресса. Клик по ней открывает папку с результатом.
* Если отпустить файл **мимо колеса**, сработает обычное Shift+перетаскивание Проводника (перемещение).
* **Esc** во время перетаскивания отменяет всё.

## Ограничения

* OCR (TXT/DOCX) использует встроенное распознавание текста Windows и языки из настроек системы (Параметры → Язык и регион). DOCX получается с простым текстом без вёрстки.
* PDF, защищённые паролем, не открываются.
* Word, PowerPoint и Excel конвертируются через установленный на компьютере Microsoft Office; без него эти форматы недоступны. Уже открытые документы Office не закрываются.
* Колесо работает только для файлов в Проводнике и на рабочем столе.

## Как устроен

| Файл | Назначение |
|---|---|
| `InputWatcher.cs`, `Native.cs` | глобальные хуки мыши и клавиатуры: определяют перетаскивание в Проводнике и нажатие Shift |
| `WheelWindow.cs` | радиальное меню: прозрачное окно, принимающее drop; сектора строятся под тип файла |
| `Formats.cs`, `Converter.cs` | таблица форматов и конвертация |
| `PdfTools.cs` | рендер PDF, OCR, картинки в PDF на встроенных API Windows |
| `OfficeTools.cs` | Word, PowerPoint и Excel через COM-автоматизацию Office |
| `TrayHost.cs`, `ToastHost.cs` | иконка в трее, автозапуск, карточки статуса |

## Командная строка и отладка

```
ShiftWheel.exe --convert <файл> fmt:PNG
ShiftWheel.exe --convert <файл> tool:compress     (strip | resize | rotate)
```

Лог работы колеса: задайте переменную `SHIFTWHEEL_DEBUG=1` перед запуском, лог будет в `%TEMP%\shiftwheel.log`.

## Сборка

```
cd ShiftWheel
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Как сделан

Проект сделан вместе с Claude (Claude Code): идея и постановка задач мои, код написан с помощью ИИ, результат я проверял запуском. Здесь это отдельный личный инструмент, а не пример моего собственного кода: самостоятельно написанный backend-проект — [LearningTracker](https://github.com/DaoSS-SYS/LearningTracker).
