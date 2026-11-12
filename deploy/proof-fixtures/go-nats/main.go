// Publishes a message through the broker it was given and reads it back, on every request.
package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"runtime"
	"time"

	"github.com/nats-io/nats.go"
)

type answer struct {
	Ok        bool   `json:"ok"`
	Echo      string `json:"echo,omitempty"`
	Server    string `json:"server,omitempty"`
	JetStream bool   `json:"jetStream"`
	Go        string `json:"go"`
	Error     string `json:"error,omitempty"`
}

func roundTrip() answer {
	connection, err := nats.Connect(os.Getenv("NATS_URL"), nats.Timeout(5*time.Second), nats.MaxReconnects(0))
	if err != nil {
		return answer{Go: runtime.Version(), Error: "connect: " + err.Error()}
	}
	defer connection.Close()

	subscription, err := connection.SubscribeSync("proof.echo")
	if err != nil {
		return answer{Go: runtime.Version(), Error: "subscribe: " + err.Error()}
	}
	if err := connection.Publish("proof.echo", []byte("hello")); err != nil {
		return answer{Go: runtime.Version(), Error: "publish: " + err.Error()}
	}
	message, err := subscription.NextMsg(5 * time.Second)
	if err != nil {
		return answer{Go: runtime.Version(), Error: "read back: " + err.Error()}
	}

	// Whether the broker keeps messages at all, which only the broker can say.
	jetStream := false
	if stream, err := connection.JetStream(nats.MaxWait(5 * time.Second)); err == nil {
		if _, err := stream.AccountInfo(); err == nil {
			jetStream = true
		}
	}

	return answer{Ok: true, Echo: string(message.Data), Server: connection.ConnectedServerVersion(), JetStream: jetStream, Go: runtime.Version()}
}

func main() {
	http.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(roundTrip())
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	log.Printf("go-nats is listening on :%s", port)
	log.Fatal(http.ListenAndServe(":"+port, nil))
}
