# PalLLM sidecar container image.
#
# Self-contained: PalLLM.Domain owns its portable adapter surface inside
# src/PalLLM.Domain/Portable/ (see docs/CORE_LIBRARY.md), so this image
# builds from the PalLLM repo alone — no sibling checkout required.
#
# Build command (run from the repo root):
#   docker build -t palllm:latest .
#
# Run:
#   docker run --rm -p 5088:5088 \
#     -v palllm-runtime:/var/palllm \
#     palllm:latest
#
# The runtime stores session state, outbox envelopes, and TTS artifacts
# under /var/palllm; mount a named volume (shown) or a host path.
#
# ---------- stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy just this repo. .dockerignore keeps bin/obj/artifacts out.
COPY . .

# Restore + publish the sidecar project only. Other projects in the solution
# (Domain, Tests) are built transitively as dependencies but don't need
# separate publish steps for the runtime image.
RUN dotnet publish src/PalLLM.Sidecar/PalLLM.Sidecar.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

# ---------- stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Non-root user is a best practice for any long-running container.
# .NET 8+ base images ship the $APP_UID env var for this pattern.
USER $APP_UID

WORKDIR /app
COPY --from=build /app/publish ./

# Bind to all interfaces inside the container; the host's port mapping
# controls external exposure. Default PalLLM runtime root inside the
# container is /var/palllm so the operator can mount a single volume.
ENV ASPNETCORE_URLS=http://+:5088 \
    PalLLM__PalSavedRoot=/var/palllm \
    DOTNET_EnableDiagnostics=0

EXPOSE 5088
VOLUME ["/var/palllm"]

ENTRYPOINT ["dotnet", "PalLLM.Sidecar.dll"]
