Simple class for inscribe data on dogechain using Nbitcoin and C# (Doginals)

Using:
1. Install Nbitcoin to your project
2. Add this class
3. Use class for inscribe your data

Example using code:

```
DogeInscriber inscriber = new DogeInscriber("your_wif_key");  // set you private key from wallet NOT MNEMONIC

Console.WriteLine("Address: " + inscriber.GetAddress());
Console.WriteLine("Private Key: " + inscriber.GetPrivateKey());

// Sync Wallet
await inscriber.SyncWalletAsync();

Console.WriteLine("Balance: " + inscriber.GetBalance().ToDecimal(MoneyUnit.BTC) + " DOGE");

// Data for inscribe
string contentType = "text/plain;charset=utf8";
byte[] data = Encoding.UTF8.GetBytes("{\"p\":\"drc-20\",\"op\":\"mint\",\"tick\":\"oggo\",\"amt\":\"1000\"}");

// Receiver Address
string receiverAddress = "D8zmmd9wRbzk28JQSqzJs1LhjhZHnHrPJW";

// Push Tx to Dogecoin Network
string txid = await inscriber.InscribeAsync(contentType, data, receiverAddress);
Console.WriteLine("TX: " + txid);
```
Woof-woof

Donate: D8zmmd9wRbzk28JQSqzJs1LhjhZHnHrPJW

Telegram: @skr1pt_0
