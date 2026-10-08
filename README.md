# Book pressure

Quantower indicator. It reads the Binance book already connected in Quantower. It does not open a second connection.

Green bars are buyers adding. Red bars are sellers adding. The blue line is the running total since the indicator was attached.

## Download

[OrderFlowImbalance.cs](https://github.com/bandhu-netizen/book-pressure/raw/main/OrderFlowImbalance.cs)

## Use

1. Copy `OrderFlowImbalance.cs` into the Quantower Scripts folder.
2. Compile.
3. Add **Book pressure** once to a Binance chart that already shows the DOM.
4. Leave Prices to watch at 1.

The pane says Buyers adding, Sellers adding, or Balanced. Candles from before you attach it stay empty.
