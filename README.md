# Venix

Launcher de jogos para Windows (WPF / .NET 10) com visual premium cinza em vidro fosco, temas claro e escuro.

## Recursos
- **Início**: destaque rotativo com vidro fosco real sobre a arte, estatísticas e vitrine da Steam (mais vendidos, lançamentos, ofertas, em breve).
- **Catálogo**: todos os títulos das suas fontes, agrupados por jogo, com busca, filtro por fonte, ordenação e capas da Steam.
- **Página do jogo**: arte, logo, descrição, capturas, Metacritic, informações e opções de download.
- **Downloads**: torrents via MonoTorrent (pausar, retomar, cancelar, limite de velocidade, semear); links http abrem no navegador.
- **Biblioteca**: executável por jogo, botão Jogar e contagem de tempo de jogo.
- **Cheats**: ~5.600 tabelas `.ct` de [Hexorg/CheatEngineTables](https://github.com/Hexorg/CheatEngineTables) aplicadas **direto na memória pelo próprio Venix** (trainer embutido, sem programas externos): conecta sozinho ao processo do jogo, ativa/desativa scripts Auto Assembler (aobscan, alloc perto do código, injeção montada com Iced, assert, readmem, reassemble, register/unregister symbol, dealloc), lê/edita/congela valores (endereços, ponteiros, dropdowns). Scripts com Lua aparecem como não suportados. Uso apenas em modos offline/single-player.
- **Fontes**: todo `.json` da pasta `sources\` ao lado do `Venix.exe` é sincronizado na splash screen ao abrir (arquivo novo entra, arquivo apagado sai, arquivo alterado é relido).
- **Ajustes**: tema, pasta de downloads, fontes (cole várias URLs de uma vez ou importe um .json).

## Fontes de download
Formato aceito (compatível com Hydra):
```json
{ "name": "Fonte", "downloads": [ { "title": "...", "uris": ["magnet:..."], "uploadDate": "2025-01-01T00:00:00Z", "fileSize": "8.5 GB" } ] }
```
Se o servidor da fonte responder com uma página anti-bot em vez do JSON, baixe o arquivo pelo navegador e use **Importar arquivo**.

## Compilar
```bash
dotnet build src/Venix
dotnet publish src/Venix -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
Dados do usuário ficam em `%AppData%\Venix`; cache de imagens em `%LocalAppData%\Venix\images`.
