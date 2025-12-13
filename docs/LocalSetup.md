# Local Development Setup

This guide will help you set up a complete local development environment for the CLOOPS NATS SDK. The instructions are primarily given for macOS, but platform-specific alternatives are provided where applicable.

## 🚀 Quick Start

### Prerequisites

- Docker and Docker Compose
- .NET SDK (9.0 or later)
- Terminal access with admin privileges

### 1. Start NATS Server

Launch the NATS server using Docker Compose:

```bash
# From the project root directory
docker compose up -d
```

This command starts:

- NATS server with JetStream and WebSockets enabled
- NATS monitoring dashboard (accessible at http://localhost:8222)
- Configured with development-friendly settings

### 2. Install NATS CLI Tools

The NATS CLI provides essential tools for managing streams, subjects, and debugging.

#### macOS Installation

```bash
# Add NATS tools tap
brew tap nats-io/nats-tools

# Install NATS CLI
brew install nats-io/nats-tools/nats
```

#### Other Platforms

- **Windows**: Download from [NATS CLI Releases](https://github.com/nats-io/natscli/releases)
- **Linux**: Use package manager or download binary from releases
- **Manual Install**: See [Official Installation Guide](https://github.com/nats-io/natscli?tab=readme-ov-file#installation)

### 3. Configure Local Development Environment

Set up host file entries and CLI context:

```bash
# Add local development hostname
echo "127.0.0.1 dev.nats.cloops.in" | sudo tee -a /etc/hosts

# Create NATS CLI context (server must be running)
nats context add cloops-local-dev \
  --server dev.nats.cloops.in:4222 \
  --description "CLOOPS NATS Local Development" \
  --select
```

### 4. Verify Installation

Test your setup:

```bash
# Check server info
nats server info

# List available contexts
nats context ls

# Test basic connectivity
nats pub test.subject "Hello NATS!"
```

## 🧪 Running Examples and Tests

### Basic SDK Examples

Navigate to the SDK directory and run the built-in examples:

```bash
cd examples

# Test NATS Core publishing (fire-and-forget)
dotnet run pub

# Test JetStream publishing (durable messaging)
dotnet run dpub
```

### Setting Up Streams for Testing

Create a JetStream stream for testing durable messaging:

```bash
# Create a stream for Connection Loops events
nats stream create CP_Development \
  --subjects 'CP.>' \
  --retention=limits \
  --max-age=14d \
  --storage=file
```

### Testing Message Consumption

Set up a consumer to verify message delivery:

```bash
# Subscribe to specific events with durable consumer
nats subscribe "CP.cloudpathology_deesha.EffectTriggered" \
  --durable=dev-consumer \
  --deliver=all
```

## 🔧 Development Workflow

### Typical Development Session

1. **Start Environment**:

   ```bash
   docker compose up -d
   ```

2. **Develop and Test**:

   ```bash
   cd examples
   dotnet build
   dotnet run [your-test-command]
   ```

3. **Monitor Messages** (separate terminal):

   ```bash
   nats subscribe "CP.>" --durable=dev-monitor
   ```

4. **Clean Up** (when done):
   ```bash
   docker compose down
   ```

### Useful CLI Commands

```bash
# View server statistics
nats server report

# List all streams
nats stream ls

# Monitor real-time message flow
nats monitor

# Check stream info
nats stream info CP_Development

# Purge stream (careful!)
nats stream purge CP_Development

# Delete stream (careful!)
nats stream delete CP_Development
```

## 🌍 Environment Configuration

### Environment Variables

Configure your development environment with these variables:

```bash
# .env file or shell environment
NATS_URL=nats://dev.nats.cloops.in:4222
```

### Docker Compose Customization

Modify `docker-compose.yml` for custom configurations:

```yaml
# Example modifications:
services:
  nats:
    ports:
      - "4222:4222" # NATS client port
      - "8222:8222" # HTTP monitoring port
      - "6222:6222" # Routing port
    command:
      - "--jetstream"
      - "--store_dir=/data"
      - "--max_payload=1MB"
      - "--max_file_store=10GB"
```

## 🐛 Troubleshooting

### Common Issues

**Connection Refused**:

```bash
# Check if NATS server is running
docker ps | grep nats

# Check logs
docker compose logs nats
```

**CLI Context Issues**:

```bash
# List and select correct context
nats context ls
nats context select cloops-dev
```

**Port Conflicts**:

```bash
# Check what's using NATS ports
lsof -i :4222
lsof -i :8222
```

**Host Resolution**:

```bash
# Verify host file entry
ping dev.nats.cloops.in

# Alternative: use localhost directly
nats context add cloops-local --server localhost:4222
```

### Reset Environment

If you encounter persistent issues:

```bash
# Complete reset
docker compose down -v  # Removes volumes
docker compose up -d

# Recreate CLI context
nats context delete cloops-dev
nats context add cloops-dev --server dev.nats.cloops.in:4222 --select
```

## 📊 Monitoring and Debugging

### NATS Monitoring Dashboard

- **URL**: http://localhost:8222
- **Features**: Server stats, connections, subscriptions, message flow

### CLI Monitoring

```bash
# Real-time server monitoring
nats monitor

# Subscribe to all messages (debug mode)
nats subscribe ">" --raw

# Monitor specific subject patterns
nats subscribe "CP.>" --count=100
```
