# Marbots Go SDK

```bash
go get github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go
```

```go
c := marbots.New("http://localhost:5170", "")
reply, err := c.Chat(ctx, "boss-man", "Buat rencana peluncuran produk 3 langkah")

events, errs := c.Events(ctx, threadID)
for e := range events { fmt.Println(e.Type, e.Message) }
```

Created by Gravicode Studios, led by Kang Fadhil.
