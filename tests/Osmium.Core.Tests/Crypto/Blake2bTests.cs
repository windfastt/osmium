using Osmium.Core.Crypto;

namespace Osmium.Core.Tests.Crypto;

/// <summary>
/// BLAKE2b (RFC 7693). Expected values from an independent BLAKE2b implementation (Python's hashlib), for messages
/// whose byte i is (i·167 + n·31 + 7) mod 256 for a message of n bytes.
/// </summary>
public class Blake2bTests
{
    private static byte[] Message(int n) => [.. Enumerable.Range(0, n).Select(i => (byte)(i * 167 + n * 31 + 7))];

    private static string Digest(ReadOnlySpan<byte> message, int length)
    {
        var d = new byte[length];
        Blake2b.Hash(message, d);
        return Convert.ToHexStringLower(d);
    }

    [Fact]
    public void TheExampleOfRfc7693AppendixAMatches() =>
        Assert.Equal("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923", Digest("abc"u8, 64));

    /// <summary>Lengths around the 128-byte block boundaries, the empty message and 100 kB, for eight digest lengths.</summary>
    public static TheoryData<int, int, string> Cases => new()
    {
        { 0, 1, "2e" },
        { 0, 4, "1271cf25" },
        { 0, 16, "cae66941d9efbd404e4d88758ea67670" },
        { 0, 20, "3345524abf6bbe1809449224b5972c41790b6cf2" },
        { 0, 32, "0e5751c026e543b2e8ab2eb06099daa1d1e5df47778f7787faab45cdf12fe3a8" },
        { 0, 48, "b32811423377f52d7862286ee1a72ee540524380fda1724a6f25d7978c6fd3244a6caf0498812673c5e05ef583825100" },
        { 0, 63, "4ded8c5fc8b12f3273f877ca585a44ad6503249a2b345d6d9c0e67d85bcb700db4178c0303e93b8f4ad758b8e2c9fd8b3d0c28e585f1928334bb77d36782e8" },
        { 0, 64, "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce" },
        { 1, 1, "d4" },
        { 1, 4, "b34bad49" },
        { 1, 16, "4670bbdd06732c42a3d1c1895f7a1668" },
        { 1, 20, "cb8a6fad17d5834fe624344ccb1e57baec58d236" },
        { 1, 32, "ca695f3543876c961b97734fef4027a4fc45ca7cc6d5f796fc980915b17caadc" },
        { 1, 48, "bf6b6b50ab1cdde0bec3696e9986974d08ec2f97525cbe861bbebb4574f8a415300e2043f5d3ae2f02ee6475dfa72d60" },
        { 1, 63, "57647b8c329852b8bdc57c6dad3c56bf8fcb05b4d310b73f1373139d81df170c50f9242cd31115db8944af7a0f45e49f1a10ecbe91632f242e55a823462286" },
        { 1, 64, "fd7c2fbe47b644ae200d86d2687ab2389d464a413e8ad71c07b87086f39f8cf824fb294fa7c00f2484e25de51b247e1b577d41c684322db39e6ec5d123dd429e" },
        { 3, 1, "b7" },
        { 3, 4, "8c727cb8" },
        { 3, 16, "dda67b819a6ba70066a8ed44970a3fd4" },
        { 3, 20, "5187912d8686d85683f9983b8a2667c34d459f1a" },
        { 3, 32, "9d8646960d2cd6b87aba709d6fe5a529ea87f90c795bdd7b590b46671a3fc6bd" },
        { 3, 48, "9ffdfca37b391847ad909eb181bf06bcc14d9e53d3b74525f7819886c289de0134e2ba6613dcd2797c5f4d056f9ae097" },
        { 3, 63, "ff9f374ccb5966ccc61f9b5058beb0f0c0f243634009649086c1794bed4360c99bb4b1015da696d489c3de135da18e07fad5796536fab70e8ddb3709fd13da" },
        { 3, 64, "2ba628336b55235dfbd082c05d60cf964435580963cf930696c968f268ca859d5b2c6459dcd6e7e6f7db36797bf12a6eed8a29a92630d1dd66e484d68b2ee475" },
        { 63, 1, "e9" },
        { 63, 4, "21cc574b" },
        { 63, 16, "a610b3d11149c7ce17a7d046b9891f35" },
        { 63, 20, "e193967c2a6d627cf753f067b748488af2f5613b" },
        { 63, 32, "4170cd4250995972e5cdf17708eff4e34638b06594d1dbefec3b470bd895b01a" },
        { 63, 48, "0a2cd45c2d0fe9d0df84f5a9930fe6448dfd8d14979b24a96d7d76bf751eb48d4fe62bf33209021ad69b4d7e20b9d865" },
        { 63, 63, "bb435c72d6dc14402fda73e4f882cec611d7778c4ea626a2d5de7b2fdd6237002aee6bcdcd52b719064948e572bc5e28b68b8097107960763dd9cccd6534f3" },
        { 63, 64, "666af693da5ea584cfbb1b3edcb8e9efd75cacd68f801177b5a376c538d5de4d99f5950f5cb8ac2afdf318b8e4c2445a02d9f44dfe806e8c4dfe5fbbc2bd405f" },
        { 64, 1, "4a" },
        { 64, 4, "c68268a1" },
        { 64, 16, "1677287d4e67a4feefc090cc946ee917" },
        { 64, 20, "a7ff505b7614dac33f0ed46cea2f4b6fc68d2cff" },
        { 64, 32, "f6bb7089fe5cfe4c697220a40ad5b4a7ce01c530cee14a1fae4a94f32ee8e1dd" },
        { 64, 48, "2a306b597a521d9bdd11f20078a6dbb0967c38b438e903abf100eeb249c6a8452a2ee2ed8ee4d4e1262125e383d51b1f" },
        { 64, 63, "51aa5e0a8d9f1d2a6899c6aec9f4d6cbb968abbe76acd7b7765e5b017218a5a8b6d8a79cf2225239a4244c9e2a3f72baa38ef7bd819b47f5a8dfca866bf7e7" },
        { 64, 64, "a8d86b2633a6d9219a2fc1bcbccdb3746d5b2b549a3814bbd4cefe6aad2e5dcbce752c755b3a432ac7bcc3eab4cc0ae252005f9d3d730a9f11ec9abb6f83b41d" },
        { 65, 1, "c1" },
        { 65, 4, "5af99e32" },
        { 65, 16, "782d762ceb58c93ee8b821416bf500e8" },
        { 65, 20, "781e6e6bcbf62d217d587c39b62c2614e0e9a2fb" },
        { 65, 32, "6f546488468ef925232eec46e55519c0019e9d71ad4eaee6908b5642485c91b2" },
        { 65, 48, "514c0b5de35254c3230bc2f0ed359fc40d3a3e76ff38821b79be5cb3971a1eb6333022d100d501869b34565bdf9c44a5" },
        { 65, 63, "17ef2d3328c99e134ce2f4f2b5a76c41361f6b94e1b17c9cb82baa38fc1eb23027347b36b7f75c36a5b78f34e4f839dbd7c6d837c8862108bba0ce64f3dab9" },
        { 65, 64, "e32b3250457b12f6cc6def8965dfa44628c3bbb9990a86d3b5e59fc59c092a63977fdb999dc9a329f852750dc39a2fafe479b7982c55f35b94cb37bfebb4df15" },
        { 111, 1, "b9" },
        { 111, 4, "b9cc75b5" },
        { 111, 16, "3cca2641b0bbdaa724ad03146f9550e1" },
        { 111, 20, "9d94aefb401335fd1b425eb645ba18cb44691d80" },
        { 111, 32, "c528a6ac18829f3200d18e2b4d120e0e9c72e34d83c1e36fc1d06ee877cbac10" },
        { 111, 48, "9b872e4625c2445faca7da8d19146b4126798d5496356b28b9258936d73187df2f10ef7f14f3480c6c97b7ee5656d24d" },
        { 111, 63, "ca75e77e2f0a07a545db73e9999d99878506932b27509e2837a37517dd02930c339d6233308d2b3f8c2e0203203a1639fbf4bf1b3e27d3632390e912849f4c" },
        { 111, 64, "ccfd271c7f2b78cd9187021b06f9f7e3160595acdc9e260047a08f9c7829b9b0bcff0c8406f5ab65b140d35bd5dcf11ec6746cf248142216897f65a906c52f79" },
        { 127, 1, "38" },
        { 127, 4, "34dba74c" },
        { 127, 16, "4b3e9a610c3be8b5049a0a558c346928" },
        { 127, 20, "943f26649f3c27b3631a61807d859ff0265bf4d7" },
        { 127, 32, "8b4d4f4e5d47535d9d4622164c517ee30c5e94baba9b0b250b5141b904e6b85b" },
        { 127, 48, "6e7f74b061024afe7674dc1f03d6642817003909c82c1507be08cf9125e81fdc2e3b1247d0b3a098bc8b725f7db6851a" },
        { 127, 63, "1644b3dfa5b6f1b60dab26a307f4f2269f5d1402393f4f01ffb6619e75f62903a75f02fa01de34ed70e7101d11adf5bcc592085ee45a7b9f0b299166de7d17" },
        { 127, 64, "a84dbcb74d8c3a8a2c282cb57dbf2e10fabf1c35c3e87f57870ef7ee681ec04263d43e60d7834d2bc25ec433776f531bf8892147da25e688f416b0bd4936994d" },
        { 128, 1, "c6" },
        { 128, 4, "c60a8150" },
        { 128, 16, "30021ab46f21de29b9aacd2f4b1d4123" },
        { 128, 20, "33e998d66a20aafa80f903e52c7ccaafd142b6e9" },
        { 128, 32, "0ba5a5f5fe5db6fbf070bea9b1b2fb7ff70832748bbd48404e52136e635f18b4" },
        { 128, 48, "2629f7d6860da2201a51a720a6cdc52455add4ce90a2a6f40b92fd5567e4847cf3e89c80bc1135ef6ef5e6f816cc167b" },
        { 128, 63, "5241b88a81f62517e9fa6accdc3751393ccf03a5c4bcc639904f8cbe4cb8aaa33e1968e32cfd8c2d9200d1ce9ad8937e7537655f4c7f1623d8e6db60aa877b" },
        { 128, 64, "0be33f6fcf3236f78861c9790eedb3df8226e6e9e048571765a6ec9a98d848e95f216aa4d5efc96ff524cc1254d4e22fae920db32f9e4304194d1ed55f85ab2d" },
        { 129, 1, "d6" },
        { 129, 4, "4c8d7015" },
        { 129, 16, "291a354d18766e69b23c4f448f47f6db" },
        { 129, 20, "a73fc9527f31f1eaa016f6deea7b47c4faadbbf4" },
        { 129, 32, "484206bb559dfef1101aec1905d98dea1c694657c93dda6d2196d07ebf431dba" },
        { 129, 48, "4ceff5232f3bc1b4945f0ecf14469a791d12f941b613e20530ae58c100d6aadf450e8a95d9941538a7c5b648abece8fd" },
        { 129, 63, "50907b864cd134cd3a1bddb058a3ea1047e6acda990ae13e4a88ec07913a4aa00f1c5b60aed52aedc370a2fb3d4f9833b4a8c3c52b1e32e9d0ac281f919506" },
        { 129, 64, "9ee297eac01c0518bccc7569748b3680586d2488fa2a511517bfbe23cddcec57d7f45224c0487307d1a27f8b66f1d89de0613c0a232cdb875be025798c67829d" },
        { 191, 1, "7a" },
        { 191, 4, "c80e4d06" },
        { 191, 16, "625224aed7af5a273dd6b1009dddf991" },
        { 191, 20, "1d87242c3e1116db392658bf04f23f68513b4387" },
        { 191, 32, "e600751eedbe8536405adf634e143012385538144462835c3c0360095660a930" },
        { 191, 48, "77cee606708abff8dd3204de52f1e012bacf67fff719212f3d15e090eaa7e8918e280c0568f4f63b712e553627d7a038" },
        { 191, 63, "725979d84b7c823ecfc2e37d758b231a89472a99ad93e4e61724fdf0960fee39f2c00cdc856dff983dac3b14c0a58a68e40997c58c371eef22ffd1084184ce" },
        { 191, 64, "8e1965b8da8b1299fe7f33ecdce6a291425713facfd6e81c780818e0d1d2b29b295e6fbc63cd6c8f802a9663f8584ea589b35ae0387b9f3a8b6953aa02497746" },
        { 255, 1, "52" },
        { 255, 4, "fa9f6ff9" },
        { 255, 16, "4f4a93764d026d3a832dbfab97083a31" },
        { 255, 20, "b8d7f1fbf4e2710e7ddb5078c5804b3f15c7dee8" },
        { 255, 32, "4e44aa71963567b89a83e5e3d25c5067d2041ac462ae2565f5a14f4d018cf9e3" },
        { 255, 48, "c85f426f6c7c1be0f13255d0efbc721a5f10865ecd87a7372227cacc4664a704087a6232c645afead3eaa0cb754cf129" },
        { 255, 63, "821128479079ef6a0696b71e5f53ed143976840c24ea00a7207edb61545687efcf698447f663bc575c5dc9c42eb1982b288c289d95b477d14e6d00af0e744b" },
        { 255, 64, "2fb921c474107cf4917d667d95e4ef8cbe04a75b6d7fed4080aa123412e31e0e9f18c79762ad8f140725691d3d84d6646e07b96ce27557996ca3abb86b756083" },
        { 256, 1, "47" },
        { 256, 4, "37ca8ff3" },
        { 256, 16, "8db0f72a0ab24ea34916973e8cc8659b" },
        { 256, 20, "f16bc93313c969e6e629f4f872fb1e7195b23a69" },
        { 256, 32, "4f2249ac43d332f5449505ce039784033cf458d822cd83ce1d0d82335b3f19f6" },
        { 256, 48, "5bdcf373e9699c3e1608c9c2fa53f3ebaf30bca3df67b3e208f5255eb9ac1e0c152ad2dcdad6a65d25e0fe10a7f8a5b5" },
        { 256, 63, "c3190e913cb77816a8e747c1ee9240ffde839135def3018d6833fa50b816a3968de2d53ee3754f79eae57510557969c141c36f240c1b47519cc83676ce0e02" },
        { 256, 64, "dbdc94719a8356986a14245c2d3a3423effc2a1149fde35b69171a28621fb318d7cd1fdc956509d526629bbf88488970be2dce0572979250c40eb8c054f2703f" },
        { 257, 1, "8e" },
        { 257, 4, "bbef8528" },
        { 257, 16, "f209f30b91d2cecd10e56c9480043ec3" },
        { 257, 20, "3d53ed1db46d2fcbeb0caaca1b9c5f80f2391430" },
        { 257, 32, "16c2c8a122c323f76b086c6b0f1dc2b1066c55d877ed95cdfe8d947080fa5197" },
        { 257, 48, "d1b237d2ca229e981366854c3d6e414bb77377446e05f8e735d0d6e99b40d1e821297533f3fdc41046ec9a602aea7e1e" },
        { 257, 63, "194dec69d9a57fa3e66f269f311e212f7c6fc7d87a18fd2d090e4650868d0efc7dd7e13d229f47fcf6304fc4a012e8b17e653fb3dd18449c391a17c70964a6" },
        { 257, 64, "ea1836b680be3fe324fb0ce46cbb80db657b87d8dee338bdcdab631a9affc759b326d6f144f7545851c0d5807ecaf59c5c2ce222b09804bb0e83802d4285282f" },
        { 1000, 1, "55" },
        { 1000, 4, "82995d69" },
        { 1000, 16, "835a2b30c5d3accd30e86210b23147e6" },
        { 1000, 20, "457b300fe7039b7bfe5e4fb13983fad3718eb400" },
        { 1000, 32, "534d9cc5bef1b592b9e3bd3bc9d00c3726d029b87e0466f54060786b7d6ad2e6" },
        { 1000, 48, "5f31818cd20759f17dd92641c4c2bda4c247831b8c1d4a5f7521bb3b37c695c596ff941dbc53e366a2b78ce445fb872f" },
        { 1000, 63, "5802d693f0ab72a2ba829cc3486064d722d8dde5e8df3b33c31f9c9f38e16c6b5462da1961c91dcf6526b75b4d320d1e69b74a8ab0a514b4b322a3bd57416a" },
        { 1000, 64, "bd65b322a1f4f4b1ae0de58456fa2498d1dad3b0cbec1d75e11a4e5ad3cc897b2d8709bcaa6e21dfe23af5b2c430403f17ba2855d0749b29126eeb6a41907cc5" },
        { 100000, 1, "54" },
        { 100000, 4, "19760d83" },
        { 100000, 16, "c89125f3705ecfe4b9eb5fd6a86ab01f" },
        { 100000, 20, "2322efee00479da96b32c5b5d69b364a9d580f47" },
        { 100000, 32, "74601745b431ecad129882f3f45d4f6b3e0a1577a9821b1c63919e2cac515985" },
        { 100000, 48, "9d04638936d0b5a39883dcc25334cbc6a2b73cbf31470b64149b1456ea164526cd16f16b2c4601ddfcb8123a6029b693" },
        { 100000, 63, "13bc317ceef16dd00333f1d9b5ec8e9e2985946e5516c12dfb1ccecffefcaeaf6148c313b8c827cc58ce73240d876fef013628d651e2b62b6361a956c4210a" },
        { 100000, 64, "d7c88a3d9d54fe1a1ef4fc31a211a69607cecd34bb70c09de9a1acea15b20b4e5d6c86a740ed36045313346b320479031ed51711decb82c83a2eb025d24aa154" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DigestsMatchTheIndependentImplementation(int messageLength, int digestLength, string expected) =>
        Assert.Equal(expected, Digest(Message(messageLength), digestLength));

    /// <summary>The same 200-byte message with every digest length from 1 to 64 bytes.</summary>
    private static readonly string[] EveryLength =
    [
        "68",
        "6220",
        "9600ed",
        "a2c1af27",
        "641836ac49",
        "01238c0dca7a",
        "e6a8cd75915d0a",
        "aaa4cf5ab1f5d49e",
        "ba724d1e42287daeb4",
        "100828fc9b0c1f8866e1",
        "73a3521f36331a5553840a",
        "9ce5b0afa56a3494a06cd77e",
        "c286cef9a767d5969c3ec66b86",
        "9f4141f1321e88764694164b8098",
        "76fd0434cfcc588e6f010f45ae5f94",
        "4837ed10582d8c1f548639610bfcfb1c",
        "d87ede32368eb1170bf1cf6843274c7f98",
        "eaeefc7593124fe0c186e92517fe142f31dc",
        "c4395f63d45dd30630e7af66a83da05ad03ed3",
        "be778756bbf0719a93e68cb42a22b5560164d55b",
        "3189610d12aa74ab4192e7bc188d0b9a147e326815",
        "af0cf50badd48e7bf89b7d0dc041a772861f4d7ba108",
        "f1f257e6f2c0fcee396170735892524533a1a62944cdd2",
        "6c0a0fb893e3b532f2c9df6d5ce35bba4633aaeeb837d8bd",
        "4e49c13a1d0b457e926263b05e793ab90d00655f211647b287",
        "318fef1c60076008e726726b2948c921e051ca6b20766a30edfb",
        "6db2df874e1f51f2632a35cfbe2375ec5e88ad8607346e7f9de766",
        "a275a9bc1f33627e146ea51a115df89d00988526b4eefd09a2f4255a",
        "4fa8938fb60c91ac60ec543fc25d69020078c718e1f37b4dcf1b1f3b85",
        "86042bef8921827c67ccc9530ff5c6763dfe65b6b28061bdd7c9453bcacb",
        "f21b90b4375a41f0cc3e9c5a05956aa335522d5122c74fd8beb55b17b4b316",
        "0f299b135c8964a561570f5d869e50c2367e1d4b1e8451e21f5469332f6bc9c6",
        "02c9d1bf05a86b207eca3ed90703b13cf3f2696357e38c94b90b3bf0f93a931180",
        "52d7640b2879eb2677088b9e6cb4468236651cfa6a0df7e575f2ea4e3c80b5a89c33",
        "8f4890ad27fdb84d053051d5ed7d09a40269a10ebe2084c63b5245e102a29bf9a96510",
        "13266345f29d4a94a0255d3cf664054963d0adbe57b838acc6c2a9bd55044004f5ebd6a6",
        "132ea47e57a41409a12306bcb7b7cfdb0d1887c44bf32f03290789b56633b9a1093501fe52",
        "d2e3e50b182a458d9129e8df013392f44da456009203442de88315aefec94b2a5f3fb7a61216",
        "194ce7bab79b0d500b63f758499bece2aa0fcfc14f4b898db6f3618442f8df05b61a7f1e02aca5",
        "ff1da038653c295dd99735c1b8845311cc57724483a67b60c3d1091bb9eebd78b592b10f34cb01f9",
        "b1065667de15f73294d5dce17afc4367e642de1a231fa74116563f32dbd9e696991a8dce4fe8d2da1e",
        "df4547ecdc079c618c91dfcbadf46d2b8064687c8b1147cd710af81ae414fca4559510b774f64d0894ed",
        "83800b5beac1b403c694486c98b01712afcacd9b6d90f7055467e927cd15258534268f2d10e2d270a2c4f7",
        "76a0cebc2d667cadef43e1fc25bca815b942d462ef8d989fbc86e00f10573d9069495d7cdda029e4ce0cee69",
        "697802ea1e2e80e831dab2518c94ca8a82d83f7c24c155941a2d8260d90b86c67009c3d3bb8562f58bdca355ca",
        "a344a76c0f302e9f81b706ae829f8bbaeec530f840fbe50ccbbc45fc938ce53a32f709c6cb0f915bc7f090f88eb5",
        "ffc6dd295b9f6af81a3496385f18fb2c91b4f200c6819158c01ff78d88af4747b53b896d10d1508a5542ae807812e5",
        "d18905445eeea9f27d27d7503a6b822081119da8fbfdc3e2e9f898c99590bcb454cb8efa01d6fc4158b9a3cba8b8e4ac",
        "bf651904c65f0409768ae2cfd9354e885afa181864833f8234d3c030cebd3fca019b2cd3396c6ac3400ae112d92121c6a2",
        "65003f7c29eafa020dfde1dea5bc51c934bb6c08cf0956ade7e4ca76d51a5437825b39945055110e837f3914cbfa2a310949",
        "74d42973c4cf5fd4e92186474a625c76155d8fdc3b608f6b3bec8f3dbdd32bace7bd662944487e58cf8a0e501cb35e6d0de690",
        "2bc4004caa1a79a3456e1b8a05188a08444d71c9d56405ad764d7f3c220d6954a85953abee74714e68a20f90d6bc94ec2c0f6044",
        "6132185d7f39cc72ea113b6fc22f8a96c491d044691223c652f0ce73265e0fb7750ab6f6df5a38634af29b35fb7a6e2a52574babee",
        "f13e8eb9db38a409b4a341aa6991d46ff46aaa1c49d567347e02c9175215926afe3d0ec662932b223ac5833057a85f21f3ee037fc659",
        "4f02dcf61c3573266e881964f79d523d5b624ff8fc9eef9081482a98acf78731f1917f610b053ecfa2392814e6e9cb0036e43c6ef1580c",
        "f0145f6ce9b1190ca9b9156811d60cd932c22a67f24fd5a1029df5c8b18a4fbc41f859301c82a2d095251ff464b83344b9f1533f400d394e",
        "9d7f10ac524fe71dcf703924782fecb929d1446d92095c63de967acde37b561f13f73fceae76847367fa597a009d4fba9c33200412c8a4837f",
        "37688a50bad89a0700e190e4d2cee623261267757284b8a22d4c78e22b96abbc5579581b06a5203bda347369775ce78513f8995d3aea0a4410c3",
        "b954f33db316ba2238001018679a9c1cb3fd2c307296e1037e43ad968a3dbd213e3837f71da989647a01d12698780edc5309a682507ce0f9a47bed",
        "71943aac46204232b66800e8685447b4783024887341fbb19d210f86658de30c5a92748d6d97049ef0d63e4ad2c6936a30909361e9b44b0a35d4a377",
        "db90afecc8d2447e69b2a3787e3e969e44cf6bbd42d955fdba52d4d1bde72490273d2ceb00c22b4bf527de7bc09886e4284d25c415bad7c5034bed42cf",
        "daaea8d29db89a03299605c6c02f60b473a18533db12ce793d7e8b8b2c5ce501b34d0397de17c500dd41b08d7d56b10b55793467cd238c42c0272ff64348",
        "40c8a6fa621dc9ba491d311a98e08f74642da4b0c28b74c2e6228d2cdaf35e545d284130a5539d923e2c0f65a61f62c0bec123f585e6957fe9c5b57d6d8a66",
        "c21bc1f8c78bfca3451bb4ccc9b3ec4143613acecf33ae1f1d0bb356407a350ec900ae040fddb6288b950c83cc3b0720ed80bab88704560d3de4859b069e42c8",
    ];

    [Fact]
    public void EveryDigestLengthFromOneTo64BytesMatches()
    {
        var m = Message(200);
        for (int length = 1; length <= 64; length++) Assert.Equal(EveryLength[length - 1], Digest(m, length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(1000)]
    public void IncrementalUpdatesEqualOneShot(int chunk)
    {
        foreach (int n in new[] { 0, 1, 127, 128, 129, 255, 256, 257, 100_000 })
        {
            var m = Message(n);
            var b = new Blake2b(64);
            for (int at = 0; at < n; at += chunk) b.Update(m.AsSpan(at, Math.Min(chunk, n - at)));
            var d = new byte[64];
            b.Final(d);
            Assert.Equal(Digest(m, 64), Convert.ToHexStringLower(d));
        }
    }

    [Fact]
    public void OddChunkSizesInOneMessageEqualOneShot()
    {
        var m = Message(100_000);
        var b = new Blake2b(48);
        int at = 0;
        for (int size = 1; at < m.Length; size = size * 3 % 997 + 1)
        {
            int n = Math.Min(size, m.Length - at);
            b.Update(m.AsSpan(at, n));
            at += n;
        }
        var d = new byte[48];
        b.Final(d);
        Assert.Equal(Digest(m, 48), Convert.ToHexStringLower(d));
    }

    [Fact]
    public void InvalidLengthsAndReuseAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Blake2b(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Blake2b(65));
        var b = new Blake2b(32);
        Assert.Throws<ArgumentException>(() => b.Final(new byte[31]));
        b.Final(new byte[32]);
        Assert.Throws<ObjectDisposedException>(() => b.Update([1]));
        Assert.Throws<ObjectDisposedException>(() => b.Final(new byte[32]));
    }
}
