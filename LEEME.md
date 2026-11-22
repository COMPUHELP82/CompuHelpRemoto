# CompuHelp Soporte Remoto 2.0

Programa para Windows para dar soporte remoto: ves la pantalla del cliente y manejas su mouse y su teclado.
Funciona como TeamViewer: el cliente te da su **ID** y su **contraseña**, y tú te conectas.

## 1. Compilar

1. Instala el **SDK de .NET 8**: https://dotnet.microsoft.com/download/dotnet/8.0
2. Doble clic en `compilar.bat`.
3. El programa queda en `salida\cliente\CompuHelpRemoto.exe`. Es un solo archivo y no necesita instalación.
   Cópialo a tu PC y a la del cliente.

También puedes abrir `Cliente\CompuHelpRemoto.csproj` en Visual Studio 2022.

## 2. Usarlo en la misma red (sin servidor)

- En la PC del cliente: abre el programa. Abajo dice "IP en red local: 192.168.x.x".
- En tu PC: escribe esa IP en "Controlar un equipo remoto", luego Conectar, y escribe la contraseña.
- La primera vez, Windows preguntará si permites el acceso a la red: acepta en **redes privadas**.

## 3. Usarlo por internet con ID (como TeamViewer)

Necesitas el **servidor de enlace** (`CompuHelpRelay`) en una máquina con IP pública,
por ejemplo un VPS barato (Linux o Windows) o una PC tuya con el puerto abierto en el router.

```
# Linux (VPS)
chmod +x CompuHelpRelay
./CompuHelpRelay 5651
```

- Abre el puerto **TCP 5651** en el firewall del VPS o del router.
- En el programa, en **⚙ Configuración → Servidor de enlace**, escribe por ejemplo `mi-vps.com:5651` (o la IP).
  Hazlo en tu PC y en la del cliente. Puedes dejar el config ya hecho: está en
  `%AppData%\CompuHelpRemoto\config.json`. Copia solo el campo `RelayServer`, **no** `Secret`.
- Abajo debe aparecer en verde: "Listo para conectar (conexión segura)". Ya puedes conectar por ID.

## 4. Acceso con usuario y contraseña fijos (sin que nadie acepte)

Sirve para entrar a equipos de tus clientes cuando no hay nadie, como el "acceso desatendido" de TeamViewer.

En la PC del cliente, una sola vez:
1. Abre **⚙ Configuración**.
2. En **Usuario** pon un nombre fácil, por ejemplo `oficina-juan`.
3. Pon una **contraseña fija** de al menos 8 caracteres, dos veces.
4. Marca **Iniciar CompuHelp Remoto con Windows**.
5. Guarda.

Desde tu PC, escribe `oficina-juan` en "Controlar un equipo remoto", luego Conectar, y pon la contraseña fija.
- Con la contraseña fija **no aparece** el mensaje "¿Permitir el acceso?".
- Con la contraseña temporal sigue apareciendo, si esa opción está activada.
- El usuario funciona por internet, así que necesitas el servidor de enlace. En la red local usa la IP.
- Cada usuario queda ligado al primer equipo que lo registra. El servidor lo guarda en `usuarios.json`.
- La contraseña fija no se guarda tal cual. Solo se guarda una clave derivada de ella.

## 5. Compartir archivos

**Desde tu PC (técnico):**
- Botón **📁 Archivos** en la ventana remota: abre un explorador del otro equipo. Ahí puedes:
  - Ver el Escritorio, Documentos, Descargas, Imágenes y los discos.
  - Descargar archivos con doble clic o con **⬇ Descargar a mi PC**.
  - Subir archivos a la carpeta abierta con **⬆ Enviar archivos aquí**, o arrastrándolos a la lista.
- También puedes **arrastrar archivos sobre la pantalla remota**. Llegan a `Descargas\CompuHelp` del otro equipo.
- Lo que descargas queda en tu carpeta `Descargas\CompuHelp`.

**Desde la PC del cliente:**
- Botón **Enviar archivo** en la barra roja: el archivo te llega a tu `Descargas\CompuHelp`.
- El cliente no puede escoger dónde se guarda en tu PC. Esto es por seguridad.

Los archivos viajan cifrados igual que la pantalla.

## Seguridad

- Cada sesión va **cifrada de punta a punta con AES-256-GCM**. La clave sale de la contraseña,
  así que el servidor de enlace solo pasa los datos y no puede ver la pantalla ni las teclas.
- Si alguien pone mal la contraseña 5 veces, el equipo bloquea las conexiones por 1 minuto.
- El ID está ligado a una clave privada guardada en cada PC: nadie puede hacerse pasar por otro ID.
- Por defecto el cliente **debe aceptar** cada conexión ("¿Permitir el acceso?").
  Se puede desactivar en Configuración si es un equipo tuyo.
- Mientras hay sesión, el cliente ve una barra roja con el botón **Desconectar**.
- Con el botón ↻ se crea una contraseña nueva. También cambia cada vez que se abre el programa.

> Úsalo solo en equipos de clientes que te den permiso.

## Atajos en la ventana remota

- **F11**: pantalla completa.
- Casi todas las teclas se envían al equipo remoto, también Alt+F4, Ctrl+C, etc.
  La tecla Windows y Ctrl+Alt+Supr se quedan en tu PC.

## Limitaciones de esta versión 1.0

- Se transmite el **monitor principal**.
- Para manejar ventanas que piden **administrador** (UAC), abre el programa en la PC del cliente
  con "Ejecutar como administrador". La pantalla segura del UAC (fondo oscuro) no se puede ver.
- Las carpetas se envían archivo por archivo, no la carpeta completa de una vez.
- Todavía no tiene chat, portapapeles compartido ni audio.
- Las versiones 1.0 y 2.0 no se pueden conectar entre sí. Actualiza el programa en las dos PCs y también el servidor.

## Estructura

```
Cliente/   programa de Windows (WinForms, .NET 8)
  MainForm.cs         ventana principal (ID, contraseña, conectar, configuración)
  HostService.cs      parte que recibe la conexión y comparte la pantalla
  ScreenCapturer.cs   captura la pantalla y envía solo los bloques que cambian (JPEG)
  InputInjector.cs    mueve el mouse y presiona teclas en el equipo remoto
  ViewerForm.cs       ventana del técnico (ve la pantalla y controla)
  ViewerConnector.cs  conexión por ID, usuario o IP
  FileTransfer.cs     envío y recepción de archivos
  FilesForm.cs        explorador de archivos del equipo remoto
  SecureChannel.cs    protocolo y cifrado
Relay/     servidor de enlace (consola, Windows o Linux)
```
