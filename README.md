# Lost in Vivo — русификатор RTLC TEAM

Русская локализация для Lost in Vivo на BepInEx 5. Включает перевод интерфейса, предметов, записок, диалогов и текстур, кириллические шрифты и исправления отображения текста.

## Скачать и установить

Откройте [последний релиз](https://github.com/Hayrizan/RTLC.LostInVivo.RussianTranslate/releases/latest) и скачайте `RTLC.LostInVivo.RussianTranslate-0.2.2-Full-BepInEx-x64.zip`. Распакуйте содержимое рядом с `LIV.exe`, объедините папки и подтвердите замену файлов. В комплект уже входят BepInEx 5.4.23.5 x64 и Doorstop 4.5.0.

Для пользователей, которым нужны исходники текстур и PSD, в том же релизе приложен `Fonts and textures.zip`.

## Настройки

В файле `BepInEx/config/com.rtlc.lostinvivo.russian-translation.cfg` можно выбрать:

```ini
[Textures]
Enabled = true
```

Укажите `false`, чтобы оставить оригинальные игровые текстуры. Перевод текста и шрифты продолжат работать. Изменение применяется после перезапуска игры.

Журнал BepInEx находится в `BepInEx/LogOutput.log`.

## Сборка исходников

Целевая версия игры — Unity 2018.2.21f1, Windows x64, Mono/.NET 3.5. Для DLL необходимы локальные сборки игры и BepInEx 5.4.23.5; для AssetBundle — Unity Editor 2018.2.21f1. Игровые файлы в репозитории не хранятся.

Пример для PowerShell (замените пути на свои):

```powershell
./tools/build.ps1 `
  -GameDirectory 'D:\SteamLibrary\steamapps\common\Lost in Vivo' `
  -LoaderDirectory "$env:USERPROFILE\.cache\lost-in-vivo-ru\loader" `
  -UnityEditor 'D:\Unity\2018.2.21f1\Editor\Unity.exe' `
  -Deploy:$false
```

Для пересборки полного ZIP скачайте официальный архив BepInEx 5.4.23.5 x64 и используйте собранную двухфайловую папку `dist/RTLC.LostInVivo.RussianTranslate`:

```powershell
python tools/build_full_package.py `
  --plugin dist/RTLC.LostInVivo.RussianTranslate `
  --loader 'D:\Downloads\BepInEx_win_x64_5.4.23.5.zip'
```

Скрипт сверяет контрольную сумму загрузчика, архитектуру Doorstop, целостность архива и состав релиза. Если передан `--game`, дополнительно сравнивает loader и плагин с установленной игрой.

## Структура

- `src/` — BepInEx-плагин и обработчики текста, шрифтов, текстур и записок.
- `translations/` — исходные словари и правила форматирования.
- `assets/fonts/` — шрифты и TMP-атласы.
- `assets/textures/` — пользовательские переведённые PNG и манифест.
- `tools/` — сборка, проверка каталога, AssetBundle и локализационных ресурсов.
- `docs/` — заметки о совместимости, шрифтах и выполненных исправлениях.

## Благодарности и права

Игра создана Lag Studios. Русификатор и переведённые материалы подготовлены RTLC TEAM при инженерной помощи OpenAI Codex. BepInEx, Unity Doorstop, HarmonyX и шрифты принадлежат их авторам; соответствующие уведомления находятся в `licenses/third_party/` и внутри установочного ZIP.

Файлы игры и игровые сборки в этом репозитории отсутствуют. Права на игру и её оригинальные материалы остаются у правообладателей. Отдельная открытая лицензия на код и материалы русификатора не заявлена.
