package main

import (
	"net"
)

type GameEngine interface {
	Init()
	GetGameState() string
	ValidateMove(move string) error
	ApplyMove(move string)
	IsGameOver() (bool, int) // returns (isOver, winner)
}

type BoardState struct {
	Cells   [8][8]string
	Turn    int
	History []string
}

func main() {
	net.Dial("tcp", "google.com:80")
}
