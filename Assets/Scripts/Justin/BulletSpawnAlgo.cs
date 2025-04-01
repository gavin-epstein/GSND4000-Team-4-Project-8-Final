using UnityEngine;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;

[Inspectable]
public struct Bullet
{
    public Vector2 position; // the position of the bullet on the board
    public int direction; // up = 0, left = 1, down = 2, right = 3
}

public class BulletSpawner : Unit
{
    enum BoardState
    {
        Empty = 0,
        Player = 1,
        BulletUp = 1 << 1,
        BulletLeft = 1 << 2,
        BulletDown = 1 << 3,
        BulletRight = 1 << 4,
        Bullet = BulletUp | BulletLeft | BulletDown | BulletRight,
    }

    class BoardNode
    {
        // Connections to nodes on the previous turn
        public List<BoardNode> prev = new List<BoardNode>();

        // Connections to nodes on the next turn
        public List<BoardNode> next = new List<BoardNode>();
    }

    [DoNotSerialize]
    public ControlInput inputTrigger;

    [DoNotSerialize]
    public ControlOutput outputTrigger;

    [DoNotSerialize]
    public ValueInput board;

    [DoNotSerialize]
    public ValueInput numBulletsToSpawn;

    [DoNotSerialize]
    public ValueOutput bulletsToSpawn;

    private List<Bullet> outputBullets;

    protected override void Definition()
    {
        inputTrigger = ControlInput(
            "SpawnBullets",
            (flow) =>
            {
                int[,] boardData = flow.GetValue<int[,]>(board);
                int numBullets = flow.GetValue<int>(numBulletsToSpawn);
                outputBullets = GetBulletSpawns(boardData, numBullets);
                return outputTrigger;
            }
        );
        outputTrigger = ControlOutput("Output");

        board = ValueInput<int[,]>("board", new int[7, 7]);
        numBulletsToSpawn = ValueInput<int>("numBulletsToSpawn", 0);
        bulletsToSpawn = ValueOutput<List<Bullet>>("bulletsToSpawn", (flow) => outputBullets);

        Succession(inputTrigger, outputTrigger);
        Assignment(inputTrigger, bulletsToSpawn);
    }

    // This function will attempt to find a set of bullet spawns
    // for the specific board state up to the number of bullets specified.
    // If there are not enough valid spawns, it will return as many as possible.
    private List<Bullet> GetBulletSpawns(int[,] board, int numBulletsToSpawn)
    {
        // First, create a directed graph where each node is a tile and each layer is a turn.
        // A connection between two nodes is a valid move for the player that will not hit a bullet.
        // The array is indexed by (turn, row, col) or (turn, y, x)
        int boardSize = board.GetLength(0);
        BoardNode[,,] boardGraph = new BoardNode[boardSize, boardSize, boardSize];
        for (int i = 0; i < boardSize; i++)
        {
            for (int j = 0; j < boardSize; j++)
            {
                for (int k = 0; k < boardSize; k++)
                {
                    boardGraph[i, j, k] = new BoardNode();
                }
            }
        }

        // Find the player
        int playerRow = -1;
        int playerCol = -1;
        for (int i = 0; i < boardSize; i++)
        {
            for (int j = 0; j < boardSize; j++)
            {
                if ((board[i, j] & (int)BoardState.Player) > 0)
                {
                    playerRow = i;
                    playerCol = j;
                    break;
                }
            }
        }
        if (playerRow == -1 || playerCol == -1)
        {
            throw new Exception("Player not found on board");
        }

        BoardNode playerNode = boardGraph[0, playerRow, playerCol];
        playerNode.prev.Add(playerNode);

        // Create the board states in advance
        int[][,] boardStates = new int[boardSize][,];
        boardStates[0] = board;
        for (int turn = 0; turn < boardSize - 1; turn++)
        {
            boardStates[turn + 1] = AdvanceBoard(boardStates[turn]);
        }

        // For each turn, the nodes that have connections to the previous turn
        // are the nodes that the player can get to. For each of those nodes,
        // check the 4 adjacent tiles on the next turn to see if they are valid moves.
        for (int turn = 0; turn < boardSize - 1; turn++)
        {
            for (int row = 0; row < boardSize; row++)
            {
                for (int col = 0; col < boardSize; col++)
                {
                    BoardNode currentNode = boardGraph[turn, row, col];
                    if (currentNode.prev.Count == 0)
                        continue; // No connections to the previous turn, skip this node

                    // Check the 4 adjacent tiles
                    for (int d = 0; d < 4; d++)
                    {
                        int nextRow = row + (d == 0 ? -1 : (d == 2 ? 1 : 0));
                        int nextCol = col + (d == 1 ? -1 : (d == 3 ? 1 : 0));
                        bool validIdx =
                            nextRow >= 0
                            && nextRow < boardSize
                            && nextCol >= 0
                            && nextCol < boardSize;
                        bool noBullet =
                            (boardStates[turn + 1][nextRow, nextCol] & (int)BoardState.Bullet) == 0;
                        if (validIdx && noBullet)
                        {
                            BoardNode nextNode = boardGraph[turn + 1, nextRow, nextCol];
                            currentNode.next.Add(nextNode);
                            nextNode.prev.Add(currentNode);
                        }
                    }
                }
            }
        }

        // Now we check each possible bullet spawn location and see if it is valid.
        // A valid spawn location is one that will not completely block player paths, that is,
        // the last layer of the graph must have at least one connection to the previous layer.
        // Bullets can only spawn on the extreme edges of the board and direct into the board
        List<Vector2> spawnLocations = new List<Vector2>();
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                // Check if the tile is on the edge of the board
                if (row == 0 || row == boardSize - 1 || col == 0 || col == boardSize - 1)
                {
                    spawnLocations.Add(new Vector2(col, row));
                }
            }
        }

        // List of list of indices of valid spawn locations
        // Each list of indices represents a set of bullets that can be spawned
        List<List<int>> validSpawnSets = new List<List<int>>();
        for (int i = 0; i < spawnLocations.Count; i++)
        {
            // Check if the spawn location is valid
            if (IsValidSpawn(boardGraph, spawnLocations[i]))
            {
                // If it is valid, add it to the list of valid spawn locations
                List<int> spawnSet = new List<int>();
                spawnSet.Add(i);
                validSpawnSets.Add(spawnSet);
            }
        }

        // Add more bullets until you can't add any more
        while (validSpawnSets[0].Count < numBulletsToSpawn)
        {
            List<List<int>> newValidSpawnSets = new List<List<int>>();
            for (int i = 0; i < validSpawnSets.Count; i++)
            {
                List<int> spawnSet = validSpawnSets[i];
                // look forward from the index of the last bullet in the spawn set
                // to avoid duplicates
                for (int j = spawnSet[spawnSet.Count - 1] + 1; j < spawnLocations.Count; j++)
                {
                    // Check if the spawn location is valid
                    if (IsValidSpawn(boardGraph, spawnLocations[j]))
                    {
                        // If it is valid, add it to the list of valid spawn locations
                        List<int> newSpawnSet = new List<int>(spawnSet);
                        newSpawnSet.Add(j);
                        newValidSpawnSets.Add(newSpawnSet);
                    }
                }
            }
            if (newValidSpawnSets.Count == 0)
            {
                break; // No more valid spawn sets, break out of the loop
            }
            validSpawnSets = newValidSpawnSets; // Update the valid spawn sets
        }

        // Convert the valid spawn sets to bullet objects
        List<Bullet> bullets = new List<Bullet>();
        for (int i = 0; i < validSpawnSets[0].Count; i++)
        {
            int spawnIdx = validSpawnSets[0][i];
            Vector2 spawnPos = spawnLocations[spawnIdx];
            Vector2 posIdx = IndexToPosition(spawnPos, boardSize);
            int direction = 0;
            // up = 0, left = 1, down = 2, right = 3
            if (spawnPos.y == boardSize - 1)
            {
                direction = 0; // up
            }
            else if (spawnPos.x == boardSize - 1)
            {
                direction = 1; // left
            }
            else if (spawnPos.y == 0)
            {
                direction = 2; // down
            }
            else if (spawnPos.x == 0)
            {
                direction = 3; // right
            }
            Bullet bullet = new Bullet { position = posIdx, direction = direction, };
            bullets.Add(bullet);
        }
        return bullets;
    }

    // Converts a 2D array index to a 2D position on the board
    // (0,0) is the top left of the array,
    // (0,0) is the center of the board.
    /* Example: size = 3
    *   (0,0) (1,0) (2,0)    (-1,1)  (0,1)  (1,1)
    *   (0,1) (1,1) (2,1) -> (-1,0)  (0,0)  (1,0)
    *   (0,2) (1,2) (2,2)    (-1,-1) (0,-1) (1,-1)
    */
    private Vector2 IndexToPosition(Vector2 idx, int size)
    {
        int center = size / 2;
        return new Vector2(idx.x - center, center - idx.y);
    }

    /* Example: size = 3
    *   (-1,1)  (0,1)  (1,1)     (0,0) (1,0) (2,0)
    *   (-1,0)  (0,0)  (1,0)  -> (0,1) (1,1) (2,1)
    *   (-1,-1) (0,-1) (1,-1)    (0,2) (1,2) (2,2)
    */
    private Vector2 PositionToIndex(Vector2 pos, int size)
    {
        int center = size / 2;
        return new Vector2(pos.x + center, center - pos.y);
    }

    // Advances the board by one turn, moving the bullets
    private int[,] AdvanceBoard(int[,] board)
    {
        int boardSize = board.GetLength(0);
        int[,] nextBoard = new int[boardSize, boardSize];
        for (int row = 0; row < boardSize; row++)
        {
            for (int col = 0; col < boardSize; col++)
            {
                int bullet = board[row, col] & (int)BoardState.Bullet;
                if (bullet == 0)
                    continue;

                // Move the bullet in the direction it is facing
                // One tile can possibly have multiple bullets
                for (int d = 1; d <= 4; d++)
                {
                    int dir = bullet & (1 << d);
                    if (dir > 0)
                    {
                        int nextRow = row + (d == 1 ? -1 : (d == 3 ? 1 : 0));
                        int nextCol = col + (d == 2 ? -1 : (d == 4 ? 1 : 0));
                        bool validIdx =
                            nextRow >= 0
                            && nextRow < boardSize
                            && nextCol >= 0
                            && nextCol < boardSize;
                        if (validIdx)
                        {
                            nextBoard[nextRow, nextCol] |= dir;
                        }
                    }
                }
            }
        }
        return nextBoard;
    }

    // Checks if spawning a bullet at the given location will completely cut off
    // all paths for the player. This is done by removing node connections at each
    // turn the bullet will be on.
    private bool IsValidSpawn(BoardNode[,,] boardGraph, Vector2 posIdx)
    {
        // determine the direction of the bullet
        Vector2 direction = new Vector2(0, 0);
        int width = boardGraph.GetLength(2);
        int height = boardGraph.GetLength(1);
        if (posIdx.x == 0)
        {
            direction = new Vector2(1, 0); // right
        }
        else if (posIdx.x == width - 1)
        {
            direction = new Vector2(-1, 0); // left
        }
        else if (posIdx.y == 0)
        {
            direction = new Vector2(0, 1); // down
        }
        else if (posIdx.y == height - 1)
        {
            direction = new Vector2(0, -1); // up
        }

        Queue<BoardNode> queue = new Queue<BoardNode>();

        // find all the nodes that the bullet will be on
        for (int turn = 0; turn < boardGraph.GetLength(0); turn++)
        {
            Vector2 currentPos = posIdx + direction * turn;
            int row = (int)currentPos.y;
            int col = (int)currentPos.x;
            BoardNode currentNode = boardGraph[turn, row, col];
            // check if the node is reachable, don't add it to the queue if it is not
            // to avoid extra work
            if (currentNode.prev.Count != 0)
            {
                queue.Enqueue(currentNode);
                // remove all connections to the previous turn
                foreach (BoardNode prevNode in currentNode.prev)
                {
                    prevNode.next.Remove(currentNode);
                }
                currentNode.prev.Clear();
            }
        }

        // Remove all forward connections from the nodes
        while (queue.Count > 0)
        {
            BoardNode currentNode = queue.Dequeue();
            foreach (BoardNode nextNode in currentNode.next)
            {
                nextNode.prev.Remove(currentNode);
                if (nextNode.prev.Count == 0)
                {
                    queue.Enqueue(nextNode); // add to queue if it has no more connections
                }
            }
            currentNode.next.Clear();
        }

        // Check if there are any connections to the last turn
        int lastTurn = boardGraph.GetLength(0) - 1;
        for (int row = 0; row < boardGraph.GetLength(1); row++)
        {
            for (int col = 0; col < boardGraph.GetLength(2); col++)
            {
                BoardNode currentNode = boardGraph[lastTurn, row, col];
                if (currentNode.prev.Count > 0)
                {
                    return true; // valid spawn
                }
            }
        }
        return false; // invalid spawn, all paths are blocked
    }
}
