# DVR Manager

Aplicação multiplataforma em C#/.NET 8 para cadastrar câmeras IP e armazenar streams RTSP em arquivos MP4 segmentados.

## Recursos

- Adicionar, editar e remover câmeras.
- Iniciar e parar gravações individualmente.
- Reiniciar automaticamente câmeras habilitadas.
- Separar vídeos por câmera e data.
- Escolher um diretório de armazenamento diferente para cada câmera.
- Limitar o espaço total e excluir primeiro os vídeos mais antigos.
- Painel web responsivo para Windows, Linux e macOS.

## Requisitos

- [.NET 8 SDK ou Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- [FFmpeg](https://ffmpeg.org/download.html) disponível no `PATH`
- URL RTSP da câmera. ONVIF normalmente é usado para descobrir/configurar o dispositivo; a gravação de mídia ocorre por RTSP.

## Executar

```powershell
cd DvrManager
dotnet run
```

Abra o endereço exibido no terminal, normalmente `http://localhost:5000`.

## Configuração

Edite a seção `Dvr` de `DvrManager/appsettings.json`:

- `StoragePath`: diretório onde os vídeos serão gravados.
- `FfmpegPath`: executável do FFmpeg ou seu caminho completo.
- `MaxStorageGb`: limite total; use `0` para não excluir automaticamente.

Os dados das câmeras ficam em `DvrManager/data/cameras.json`. Nesta versão local, as credenciais são persistidas nesse arquivo; proteja suas permissões de acesso no sistema operacional.

Cada câmera pode sobrescrever o `StoragePath` global pelo painel de configuração. Quando o campo fica vazio, a câmera continua usando o diretório padrão. Caminhos relativos são resolvidos a partir do diretório de execução da aplicação.

## Publicar

Exemplos de publicação independente do runtime:

```powershell
dotnet publish DvrManager/DvrManager.csproj -c Release -r win-x64 --self-contained true
dotnet publish DvrManager/DvrManager.csproj -c Release -r linux-x64 --self-contained true
dotnet publish DvrManager/DvrManager.csproj -c Release -r osx-x64 --self-contained true
dotnet publish DvrManager/DvrManager.csproj -c Release -r osx-arm64 --self-contained true
```
